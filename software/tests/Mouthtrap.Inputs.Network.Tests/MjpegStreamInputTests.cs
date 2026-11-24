using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Inputs.Network.Tests;

public sealed class MjpegStreamInputTests : IDisposable
{
    private static readonly StreamUrl Camera = ResultAssert.Ok(StreamUrl.Create("http://camera/stream"));
    private const string Token = "abcdefghijklmnop-_0123";
    private static readonly byte[] Jpeg = [0xff, 0xd8, 1, 0xff, 0xd9];
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-token-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Run_TokenFile_AuthenticatesStreamAndBorrowsHandler()
    {
        // Given
        using var handler = new Handler();
        var frames = new LatestFrameBuffer();

        // When
        var exit = await Run(new(Camera, TokenFile($"  {Token}\r\n"), handler), frames);

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), handler.Authorization);
        Assert.Equal(1, frames.Statistics.Received);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Run_NoTokenFile_SendsNoCredentials()
    {
        // Given
        using var handler = new Handler();

        // When
        var exit = await Run(new(Camera, null, handler));

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.True(handler.Requested);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Run_MissingTokenFile_FailsWithSettingsErrorBeforeConnecting()
    {
        // Given
        using var handler = new Handler();

        // When
        var exit = await Run(new(Camera, Path.Combine(directory, "missing.txt"), handler));

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputSettingsInvalid));
        Assert.False(handler.Requested);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("abcdefghijklmnop qrst")]
    [InlineData("abcdefghijklmnopあいう")]
    public async Task Run_InvalidToken_FailsWithSettingsErrorThatDoesNotEchoFile(string content)
    {
        // Given
        using var handler = new Handler();

        // When
        var exit = await Run(new(Camera, TokenFile(content), handler));

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputSettingsInvalid(var message)) && !message.Contains(content, StringComparison.Ordinal));
        Assert.False(handler.Requested);
    }

    [Fact]
    public void Read_OversizedTokenFile_Fails()
    {
        // Given
        var path = TokenFile(new string('a', StreamTokenFile.MaxFileBytes + 1));

        // When / Then
        ResultAssert.Err(StreamTokenFile.Read(path));
    }

    [Fact]
    public void Read_FolderInsteadOfFile_FailsAsUnreadableSettings()
    {
        // When
        var token = StreamTokenFile.Read(directory);

        // Then
        Assert.StartsWith("The token file cannot be read", ResultAssert.Err(token).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TokenRejected_FailsAsUnavailableInput()
    {
        // Given
        using var handler = new Handler { Status = HttpStatusCode.Unauthorized };

        // When
        var exit = await Run(new(Camera, TokenFile(Token), handler));

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputUnavailable(_, 401)));
    }

    private string TokenFile(string content)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<Exit<Unit, FrameSourceError>> Run(MjpegStreamInput input, LatestFrameBuffer? frames = null) =>
        await input.Run(frames ?? new()).RunExitAsync(new(TimeProvider.System), CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Requested { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = true;
            Authorization = request.Headers.Authorization;
            byte[] body = [.. Encoding.ASCII.GetBytes("\r\n--cam\r\nContent-Type: image/jpeg\r\n\r\n"), .. Jpeg, .. Encoding.ASCII.GetBytes("\r\n--cam--\r\n")];
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/x-mixed-replace; boundary=cam");
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
