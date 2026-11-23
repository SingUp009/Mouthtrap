using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Inputs.Network.Tests;

public sealed class MjpegSourceTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 1, 0xff, 0xd9];

    [Theory]
    [InlineData("multipart/x-mixed-replace; boundary=cam", true)]
    [InlineData("multipart/x-mixed-replace; boundary=\"cam\"", false)]
    public async Task Run_FragmentedFrames_OwnsFrameReleasesResponseAndBorrowsClient(string type, bool length)
    {
        // Given
        var bytes = Multipart(Jpeg, length ? "Content-Length: 5\r\n" : "");
        var stream = new FragmentedStream(bytes);
        using var handler = new Handler(stream, type);
        using var client = new HttpClient(handler, disposeHandler: false);
        var frames = new LatestFrameBuffer();

        // When
        var exit = await new MjpegFrameSource(new("http://camera/stream"), client).Run(frames)
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.Equal(1, frames.Statistics.Received);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(Jpeg, Assert.IsType<EncodedFrameImage>(frames.Take()!.Image).Data);
        Assert.True(stream.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("multipart/x-mixed-replace")]
    [InlineData("multipart/x-mixed-replace; boundary=abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz")]
    public async Task Run_InvalidResponseType_FailsWithDataErrorAndClosesResponse(string type)
    {
        // Given
        var stream = new FragmentedStream(Multipart(Jpeg, ""));
        using var handler = new Handler(stream, type);
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n")]
    [InlineData("Content-Length: 4\r\n")]
    [InlineData("Content-Length: 2097153\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length: 5\r\n")]
    public async Task Run_InvalidLength_FailsWithoutPublishing(string header)
    {
        // Given
        using var handler = new Handler(new FragmentedStream(Multipart(Jpeg, header)));
        using var client = new HttpClient(handler, false);
        var frames = new LatestFrameBuffer();

        // When
        var exit = await Run(client, frames);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
        Assert.Equal(0, frames.Statistics.Received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_OversizedBodyOrHeaders_FailsWithDataError(bool headers)
    {
        // Given
        var image = headers ? Jpeg : new byte[MjpegFrameSource.MaxFrameBytes + 1];
        var extra = headers ? "X-Long: " + new string('x', 9000) + "\r\n" : "";
        using var handler = new Handler(new FragmentedStream(Multipart(image, extra)));
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
    }

    [Fact]
    public async Task Run_UnexpectedDisconnect_FailsWithLostInput()
    {
        // Given
        using var handler = new Handler(new FragmentedStream(Encoding.ASCII.GetBytes("--cam\r\nContent-Type: image/jpeg\r\n\r\npartial")));
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputLost));
    }

    [Fact]
    public async Task Run_HttpFailure_FailsAsUnavailableAndClosesBody()
    {
        // Given
        var stream = new FragmentedStream([]);
        using var handler = new Handler(stream) { Status = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputUnavailable(_, 503)));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Run_CancelledDuringStalledBody_InterruptsAndClosesBody()
    {
        // Given
        var stream = new BlockingStream();
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler, false);
        using var cancellation = new CancellationTokenSource();
        var run = new MjpegFrameSource(new("http://camera/stream"), client).Run(new())
            .RunExitAsync(new(TimeProvider.System), cancellation.Token).AsTask();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // When
        cancellation.Cancel();

        // Then
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_HeaderOrBodyTimesOutOnSuppliedClock_FailsWithTypedError(bool headers)
    {
        // Given
        var stream = new BlockingStream();
        using var handler = new Handler(stream) { StallHeaders = headers };
        using var client = new HttpClient(handler, false);
        var clock = new TimerAwareTimeProvider();
        var run = new MjpegFrameSource(new("http://camera/stream"), client).Run(new())
            .RunExitAsync(new(clock), CancellationToken.None).AsTask();
        // The header timeout (10 s) exists before the request is sent; the body timeout (5 s) before the stream is read.
        if (headers) await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        else await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // When
        clock.Advance(TimeSpan.FromSeconds(10));

        // Then
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(headers ? exit is Err<FrameSourceError>(InputUnavailable) : exit is Err<FrameSourceError>(InputLost));
        if (!headers) Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Run_ConsumerFailsDuringStalledRead_ReportsConsumerFailureNotTimeout()
    {
        // Given
        var stream = new BlockingStream();
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler, false);
        var frames = new LatestFrameBuffer();
        var failure = new InvalidOperationException("consumer failed");
        var clock = new FakeTimeProvider();
        var run = new MjpegFrameSource(new("http://camera/stream"), client).Run(frames)
            .RunExitAsync(new(clock), CancellationToken.None).AsTask();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // When
        frames.Fail(failure);
        // The stalled read only ends when its timeout fires; the consumer's failure must still win.
        clock.Advance(TimeSpan.FromSeconds(5));

        // Then
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(failure, Assert.IsType<Die>(exit.Value).Exception);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Run_CompleteLengthDelimitedJpeg_PublishesBeforeNextBoundary()
    {
        // Given
        byte[] image = [0xff, 0xd8, .. Enumerable.Repeat((byte)1, 1020), 0xff, 0xd9];
        byte[] prefix = [.. Encoding.ASCII.GetBytes("--cam\r\nContent-Type: image/jpeg\r\nContent-Length: 1024\r\n\r\n"), .. image];
        var stream = new BlockingStream(prefix);
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler, false);
        using var cancellation = new CancellationTokenSource();
        var frames = new LatestFrameBuffer();

        // When
        var run = new MjpegFrameSource(new("http://camera/stream"), client).Run(frames)
            .RunExitAsync(new(TimeProvider.System), cancellation.Token).AsTask();

        // Then
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, frames.Statistics.Received);
            Assert.Equal(image, Assert.IsType<EncodedFrameImage>(frames.Take()!.Image).Data);
        }
        finally
        {
            cancellation.Cancel();
            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
        }
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Run_DataAfterDeclaredJpegLength_FailsWithDataError()
    {
        // Given
        using var handler = new Handler(new FragmentedStream(Multipart([.. Jpeg, 0], "Content-Length: 5\r\n")));
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
    }

    [Fact]
    public async Task Run_BodyThrowsUnexpectedException_DiesWithOriginalExceptionAndReleasesResponse()
    {
        // Given
        var failure = new InvalidOperationException("unexpected");
        var stream = new FaultingStream(failure);
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.Same(failure, Assert.IsType<Die>(exit.Value).Exception);
        Assert.True(stream.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Run_RequestThrowsUnexpectedException_DiesWithOriginalExceptionAndKeepsClientUsable()
    {
        // Given
        var failure = new InvalidOperationException("unexpected");
        using var handler = new Handler(new FragmentedStream([])) { Failure = failure };
        using var client = new HttpClient(handler, false);

        // When
        var exit = await Run(client);

        // Then
        Assert.Same(failure, Assert.IsType<Die>(exit.Value).Exception);
        Assert.False(handler.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);
    }

    [Fact]
    public async Task Run_CancelledAsResponseArrives_InterruptsAndReleasesResponse()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var stream = new FragmentedStream(Multipart(Jpeg, ""));
        using var handler = new Handler(stream) { BeforeResponse = cancellation.Cancel };
        using var client = new HttpClient(handler, false);
        var frames = new LatestFrameBuffer();

        // When
        var exit = await new MjpegFrameSource(new("http://camera/stream"), client).Run(frames)
            .RunExitAsync(new(TimeProvider.System), cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Interrupted);
        Assert.True(stream.Disposed);
        Assert.Equal(0, frames.Statistics.Received);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Run_CancelledWhileWaitingForHeaders_InterruptsInsteadOfTimingOut()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(new BlockingStream()) { StallHeaders = true };
        using var client = new HttpClient(handler, false);
        var run = new MjpegFrameSource(new("http://camera/stream"), client).Run(new())
            .RunExitAsync(new(new FakeTimeProvider()), cancellation.Token).AsTask();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // When
        cancellation.Cancel();

        // Then
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_FrameOfExactlyTheLimit_PublishesItThenEndsAtEof(bool length)
    {
        // Given
        byte[] image = [0xff, 0xd8, .. new byte[MjpegFrameSource.MaxFrameBytes - 4], 0xff, 0xd9];
        var stream = new FragmentedStream(Multipart(image, length ? $"Content-Length: {image.Length}\r\n" : ""));
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler, false);
        var frames = new LatestFrameBuffer();

        // When
        var exit = await Run(client, frames);

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.Equal(1, frames.Statistics.Received);
        Assert.Equal(image.Length, Assert.IsType<EncodedFrameImage>(frames.Take()!.Image).Data.Length);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(9, true)]
    public async Task Run_HeadersArriveAroundDeadline_AcceptsOnlyThoseWithinTenSecondsAndClosesTheRest(int seconds, bool accepted)
    {
        // Given
        var clock = new FakeTimeProvider();
        var stream = new FragmentedStream(Multipart(Jpeg, ""));
        using var handler = new Handler(stream) { BeforeResponse = () => clock.Advance(TimeSpan.FromSeconds(seconds)) };
        using var client = new HttpClient(handler, false);
        var frames = new LatestFrameBuffer();

        // When
        var exit = await new MjpegFrameSource(new("http://camera/stream"), client).Run(frames)
            .RunExitAsync(new(clock), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Then
        Assert.True(accepted ? exit is Ok<Unit> : exit is Err<FrameSourceError>(InputUnavailable), exit.ToString());
        Assert.Equal(accepted ? 1 : 0, frames.Statistics.Received);
        Assert.True(stream.Disposed);
        Assert.False(handler.Disposed);
    }

    private static async Task<Exit<Unit, FrameSourceError>> Run(HttpClient client, LatestFrameBuffer? frames = null) =>
        await new MjpegFrameSource(new("http://camera/stream"), client).Run(frames ?? new())
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static byte[] Multipart(byte[] image, string headers) => [..
        Encoding.ASCII.GetBytes("\r\n--cam\r\nContent-Type: image/jpeg\r\n" + headers + "\r\n"), ..image,
        ..Encoding.ASCII.GetBytes("\r\n--cam--\r\n")];

    private sealed class Handler(Stream stream, string type = "multipart/x-mixed-replace; boundary=cam") : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool StallHeaders { get; init; }
        public Exception? Failure { get; init; }
        public Action? BeforeResponse { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Failure is { } failure) throw failure;
            if (StallHeaders) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            BeforeResponse?.Invoke();
            var response = new HttpResponseMessage(Status) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    // A body that fails with an exception no part of the source expects.
    private sealed class FaultingStream(Exception failure) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw failure;
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingStream(byte[]? prefix = null) : Stream
    {
        private int prefixAt;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (prefix is not null && prefixAt < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - prefixAt);
                prefix.AsMemory(prefixAt, count).CopyTo(buffer);
                prefixAt += count;
                return count;
            }
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

}
