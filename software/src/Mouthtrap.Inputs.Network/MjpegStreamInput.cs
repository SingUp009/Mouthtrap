using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

public static class StreamTokenFile
{
    public const int MaxFileBytes = 256;

    // The bearer token in a small text file, trimmed. The value is never in a message.
    public static Result<string, InputSettingsInvalid> Read(string path)
    {
        string? content;
        try { content = ReadLimited(path); }
        // Opening or reading the file: missing, access denied, in use, or a path that is not valid. Anything else
        // is a defect.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Err<InputSettingsInvalid>(new($"The token file cannot be read: {exception.Message}"));
        }
        return Validate(content);
    }

    private static Result<string, InputSettingsInvalid> Validate(string? content)
    {
        if (content is null) return new Err<InputSettingsInvalid>(new($"The token file must be at most {MaxFileBytes} bytes."));
        var token = content.Trim();
        return token.Length is >= 16 and <= 120 && token.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_')
            ? new Ok<string>(token)
            : new Err<InputSettingsInvalid>(new("The token must contain 16-120 ASCII letters, digits, '-' or '_'."));
    }

    // The file's text, or null when it is larger than the limit.
    private static string? ReadLimited(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxFileBytes) return null;
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }
}

// An MJPEG stream that owns its HttpClient for each run, authenticating with the token in TokenFile, which is read
// when the run starts. Handler replaces the network in tests and is borrowed, not disposed.
public sealed record MjpegStreamInput(Uri Address, string? TokenFile, HttpMessageHandler? Handler = null) : IFrameSource
{
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(LatestFrameBuffer frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            var token = TokenFile is { } path ? scope.Get(StreamTokenFile.Read(path)) : null;
            var open = Effect<PreviewEnvironment>.Sync(() =>
            {
                var client = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
                client.Timeout = Timeout.InfiniteTimeSpan;
                if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                return client;
            });
            var client = await scope.Acquire<HttpClient>(Resource.Make(open, (client, _) => Effect<PreviewEnvironment>.Sync(() =>
            {
                client.Dispose();
                return Unit.Value;
            })));
            await scope.Run(new MjpegFrameSource(Address, client).Run(frames));
            return Unit.Value;
        });
}
