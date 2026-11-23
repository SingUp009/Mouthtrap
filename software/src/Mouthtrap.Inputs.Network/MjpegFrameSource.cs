using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.WebUtilities;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

// The caller owns HttpClient; each run owns and closes its HTTP response, which owns the body stream.
public sealed record MjpegFrameSource(Uri Address, HttpClient Client) : IFrameSource
{
    public const int MaxFrameBytes = 2 * 1024 * 1024;

    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(LatestFrameBuffer frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            if (!Address.IsAbsoluteUri || Address.Scheme is not ("http" or "https"))
                throw scope.Fail(new InputSettingsInvalid("An absolute HTTP(S) stream URL is required."));
            var clock = scope.Env.Resolve<TimeProvider>();
            var response = await scope.Acquire(Response(clock));
            var boundary = scope.Get(Boundary(response));
            var reader = await scope.Run(OpenReader(response, boundary));
            Stream? pendingBody = null;
            while (true)
            {
                scope.CancellationToken.ThrowIfCancellationRequested();
                if (frames.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                // The read's error is looked at before it ends the block: a consumer that failed first explains a
                // stalled or broken read better than the read does.
                Effect<Result<Frame, FrameSourceError>, FrameSourceError, PreviewEnvironment> attempt = ReadFrame(reader, pendingBody).Attempt();
                var read = await scope.Run(attempt);
                if (read is Err<FrameSourceError> && frames.Failure is { } original) ExceptionDispatchInfo.Capture(original).Throw();
                var frame = scope.Get(read);
                if (frame.Image is null) return Unit.Value;
                pendingBody = frame.PendingBody;
                frames.Publish(frame.Image, clock.GetTimestamp());
            }
        }).OnThreadPool();

    // A JPEG, or a null image at the end of the stream. PendingBody is a length-delimited body to check for trailing
    // data when the next frame is read, so a complete JPEG is published without waiting for the next boundary.
    private readonly record struct Frame(byte[]? Image, Stream? PendingBody);

    // The response, owned by the run and released however it ends, including when cancellation races its arrival.
    private Resource<HttpResponseMessage, FrameSourceError, PreviewEnvironment> Response(TimeProvider clock) =>
        Resource.Make(Effect<PreviewEnvironment>.FromResultAsync(cancellationToken => Request(clock, cancellationToken)),
            (response, _) => Effect<PreviewEnvironment>.Sync(() =>
            {
                response.Dispose();
                return Unit.Value;
            }));

    // A connection that fails is unavailable, and so are headers that do not arrive within ten seconds, whether the
    // client gives up with a cancellation or returns a response after the deadline. A late response is closed here,
    // before anything else owns it; the caller's cancellation stays a cancellation and wins over the deadline.
    private async ValueTask<Result<HttpResponseMessage, FrameSourceError>> Request(TimeProvider clock, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        HttpResponseMessage response;
        try { response = await Client.GetAsync(Address, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false); }
        catch (HttpRequestException exception) { return new Err<FrameSourceError>(Unavailable(exception)); }
        catch (OperationCanceledException) when (Expired(timeout, cancellationToken)) { return Late(); }
        if (!Expired(timeout, cancellationToken)) return new Ok<HttpResponseMessage>(response);
        response.Dispose();
        return Late();
    }

    private static bool Expired(CancellationTokenSource timeout, CancellationToken cancellationToken) =>
        timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private static Err<FrameSourceError> Late() =>
        new(new InputUnavailable("HTTP response headers did not arrive within ten seconds."));

    // A failed status is unavailable, and a body that is not multipart/x-mixed-replace is a data error.
    private static Result<string, FrameSourceError> Boundary(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return new Err<FrameSourceError>(new InputUnavailable(
                $"The stream answered {(int)response.StatusCode} ({response.ReasonPhrase}).", (int)response.StatusCode));
        var contentType = response.Content.Headers.ContentType;
        var boundaries = contentType?.Parameters.Where(value => value.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).ToArray();
        var boundary = boundaries is { Length: 1 } ? boundaries[0].Value?.Trim('"') : null;
        return string.Equals(contentType?.MediaType, "multipart/x-mixed-replace", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(boundary) && boundary.Length <= 70 && boundary.All(value => value is >= ' ' and <= '~')
            ? new Ok<string>(boundary)
            : new Err<FrameSourceError>(new InputDataInvalid("Expected multipart/x-mixed-replace with a boundary of 1-70 ASCII characters."));
    }

    // Opening the body of a response whose headers arrived fails only when the connection does.
    private static Effect<MultipartReader, FrameSourceError, PreviewEnvironment> OpenReader(HttpResponseMessage response, string boundary) =>
        Effect<PreviewEnvironment>.FromResultAsync<MultipartReader, FrameSourceError>(async cancellationToken =>
        {
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new Ok<MultipartReader>(new(boundary, stream)
                {
                    HeadersCountLimit = 16,
                    HeadersLengthLimit = 8192,
                    BodyLengthLimit = MaxFrameBytes
                });
            }
            catch (HttpRequestException exception) { return new Err<FrameSourceError>(Unavailable(exception)); }
            catch (IOException exception) { return new Err<FrameSourceError>(new InputUnavailable(exception.Message)); }
        });

    // One frame within five seconds of the session clock; a stalled read is a lost input.
    private static Effect<Frame, FrameSourceError, PreviewEnvironment> ReadFrame(MultipartReader reader, Stream? pendingBody) =>
        Effect<PreviewEnvironment>.FromResultAsync(cancellationToken => ReadFrame(reader, pendingBody, cancellationToken))
            .Timeout(TimeSpan.FromSeconds(5), _ => new InputLost("A complete MJPEG frame did not arrive within five seconds."));

    // The one place reading converts exceptions: MultipartReader reports malformed multipart and oversized headers or
    // bodies as InvalidDataException, and a connection that ends mid-stream surfaces as IOException.
    private static async ValueTask<Result<Frame, FrameSourceError>> ReadFrame(MultipartReader reader, Stream? pendingBody,
        CancellationToken cancellationToken)
    {
        try { return await ReadSection(reader, pendingBody, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException exception) { return new Err<FrameSourceError>(new InputDataInvalid(exception.Message)); }
        catch (IOException exception) { return new Err<FrameSourceError>(new InputLost(exception.Message)); }
    }

    private static async ValueTask<Result<Frame, FrameSourceError>> ReadSection(MultipartReader reader, Stream? pendingBody,
        CancellationToken cancellationToken)
    {
        if (pendingBody is not null && await pendingBody.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            return Invalid("The multipart body exceeds its declared JPEG length.");
        var section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
        if (section is null) return new Ok<Frame>(new(null, null));
        if (!MediaTypeHeaderValue.TryParse(section.ContentType, out var type) ||
            !string.Equals(type.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            return Invalid("Each multipart section must be image/jpeg.");
        long? declaredLength = null;
        if (section.Headers?.TryGetValue("Content-Length", out var lengths) == true)
        {
            if (lengths.Count != 1 || !long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 4 || length > MaxFrameBytes) return Invalid("JPEG Content-Length is invalid or exceeds 2MiB.");
            declaredLength = length;
        }
        byte[] bytes;
        if (declaredLength is { } expected)
        {
            bytes = new byte[(int)expected];
            await section.Body.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var image = new MemoryStream();
            await section.Body.CopyToAsync(image, cancellationToken).ConfigureAwait(false);
            if (image.Length < 4 || image.Length > MaxFrameBytes) return Invalid("JPEG length is invalid or exceeds 2MiB.");
            bytes = image.ToArray();
        }
        if (bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
            return Invalid("The JPEG start/end markers are missing.");
        return new Ok<Frame>(new(bytes, declaredLength is null ? null : section.Body));
    }

    private static FrameSourceError Unavailable(HttpRequestException exception) =>
        new InputUnavailable(exception.Message, (int?)exception.StatusCode);

    private static Err<FrameSourceError> Invalid(string message) => new(new InputDataInvalid(message));
}
