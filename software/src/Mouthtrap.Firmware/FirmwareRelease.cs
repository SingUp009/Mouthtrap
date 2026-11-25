using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Firmware;

// Getting firmware from a release fails because the release cannot be reached or because what it holds cannot be
// used.
public sealed record ReleaseUnavailable(string Message, int? Code = null);
public sealed record ReleaseInvalid(string Message);

[Error]
public partial union ReleaseError(ReleaseUnavailable, ReleaseInvalid);

// Firmware from a release, checked against the release's SHA256SUMS.
public sealed record FirmwareDownload(string Version, FirmwareVariant Variant, FlashPlan Plan);

// The latest published release of Mouthtrap on GitHub. Without credentials GitHub shows no drafts, so only a release
// that a maintainer published after checking it on hardware is used. Each run owns its HttpClient; Handler replaces
// the network in tests and is borrowed, not disposed.
public sealed record FirmwareRelease(HttpMessageHandler? Handler = null)
{
    public static readonly Uri Latest = new("https://api.github.com/repos/SingUp009/Mouthtrap/releases/latest");
    public const int MaxZipBytes = 16 * 1024 * 1024;
    private const int MaxTextBytes = 1024 * 1024;

    public Effect<FirmwareDownload, ReleaseError, FirmwareEnvironment> Download(FirmwareVariant variant) =>
        Scope<ReleaseError, FirmwareEnvironment>.Gen(async scope =>
        {
            var name = FirmwarePackage.Slug(variant);
            var client = await scope.Acquire(Client());
            var release = scope.Get(ReadRelease(await scope.Run(Get(client, Latest, MaxTextBytes, "application/vnd.github+json"))));
            var asset = $"mouthtrap-firmware-{name}-{release.Version}.zip";
            if (!release.Assets.TryGetValue(asset, out var zipAddress) || !release.Assets.TryGetValue("SHA256SUMS", out var sumsAddress))
                throw scope.Fail(new ReleaseInvalid($"Release {release.Version} has no {asset} with SHA256SUMS."));
            var sums = await scope.Run(Get(client, sumsAddress, MaxTextBytes, "application/octet-stream"));
            var expected = scope.Get(Checksum(sums, asset));
            var zip = await scope.Run(Get(client, zipAddress, MaxZipBytes, "application/octet-stream"));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(zip), expected))
                throw scope.Fail(new ReleaseInvalid($"{asset} does not match the checksum in SHA256SUMS."));
            using var stream = new MemoryStream(zip, writable: false);
            var package = FirmwarePackage.FromZip(stream);
            if (package.TryGetErr(out var invalid)) throw scope.Fail(new ReleaseInvalid($"{asset} cannot be used: {invalid!.Message}"));
            return new FirmwareDownload(release.Version, variant, Value(package));
        }).OnThreadPool();

    private sealed record Release(string Version, Dictionary<string, Uri> Assets);

    private static FlashPlan Value(Result<FlashPlan, Mouthtrap.Core.InvalidValue> result) =>
        result.TryGetOk(out var plan) ? plan! : throw new InvalidOperationException("Expected a plan.");

    private Resource<HttpClient, ReleaseError, FirmwareEnvironment> Client() =>
        Resource.Make(Effect<FirmwareEnvironment>.Sync(() =>
        {
            var client = Handler is null ? new HttpClient() : new HttpClient(Handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(60);
            // GitHub's API refuses requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mouthtrap-firmware-installer", "1"));
            return client;
        }), (client, _) => Effect<FirmwareEnvironment>.Sync(() =>
        {
            client.Dispose();
            return Unit.Value;
        }));

    // The body of a successful response of at most limit bytes. A request that fails or times out is unavailable, and
    // a status other than success is unavailable with its code; the caller's cancellation stays a cancellation.
    private static Effect<byte[], ReleaseError, FirmwareEnvironment> Get(HttpClient client, Uri address, int limit, string accept) =>
        Effect<FirmwareEnvironment>.FromResultAsync<byte[], ReleaseError>(async cancellationToken =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new(accept));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    return new Err<ReleaseError>(new ReleaseUnavailable(code == 404 && address == Latest
                        ? "No release of Mouthtrap has been published yet."
                        : $"{address.Host} answered {code} ({response.ReasonPhrase}).", code));
                }
                if (response.Content.Headers.ContentLength > limit) return TooLarge(address, limit);
                using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                for (int read; (read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0;)
                {
                    if (copy.Length + read > limit) return TooLarge(address, limit);
                    copy.Write(buffer, 0, read);
                }
                return new Ok<byte[]>(copy.ToArray());
            }
            catch (HttpRequestException exception)
            {
                return new Err<ReleaseError>(new ReleaseUnavailable($"{address.Host} cannot be reached: {exception.Message}", (int?)exception.StatusCode));
            }
            catch (IOException exception)
            {
                return new Err<ReleaseError>(new ReleaseUnavailable($"The download from {address.Host} broke off: {exception.Message}"));
            }
            // HttpClient reports its own timeout as a cancellation of a token the caller did not cancel.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Err<ReleaseError>(new ReleaseUnavailable($"{address.Host} did not answer within {client.Timeout.TotalSeconds:0} seconds."));
            }
        });

    // tag_name, without the leading v, and the download URL of each asset.
    private static Result<Release, ReleaseError> ReadRelease(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag) || tag[0] != 'v' || tag.Length < 2) return Invalid($"The release tag {tag} is not v<version>.");
            var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps) continue;
                assets[name] = address;
            }
            return new Ok<Release>(new(tag[1..], assets));
        }
        // GitHub answered with something that is not the release JSON it documents.
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Invalid($"GitHub's release information cannot be read: {exception.Message}");
        }
    }

    // The SHA-256 of name in a SHA256SUMS file of "<hex>  <name>" lines, as sha256sum writes them.
    private static Result<byte[], ReleaseError> Checksum(byte[] sums, string name)
    {
        foreach (var line in Encoding.UTF8.GetString(sums).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || fields[1].TrimStart('*') != name) continue;
            return fields[0].Length == 64 && fields[0].All(char.IsAsciiHexDigit)
                ? new Ok<byte[]>(Convert.FromHexString(fields[0]))
                : Invalid($"SHA256SUMS has no valid checksum for {name}.");
        }
        return Invalid($"SHA256SUMS does not list {name}.");
    }

    private static Err<ReleaseError> TooLarge(Uri address, int limit) =>
        new(new ReleaseInvalid($"{address.Segments[^1]} is larger than {limit / 1024 / 1024} MB."));

    private static Err<ReleaseError> Invalid(string message) => new(new ReleaseInvalid(message));
}
