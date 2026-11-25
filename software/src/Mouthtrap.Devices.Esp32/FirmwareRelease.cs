using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// A published release of Mouthtrap, newest first in Releases. Prerelease marks a tag with a hyphen.
public sealed record ReleaseVersion(string Version, bool Prerelease, DateTimeOffset? Published);

// A published release of Mouthtrap on GitHub: Version, such as 1.2.0, or the latest one when Version is null.
// Without credentials GitHub shows no drafts, so only a release that a maintainer published after checking it on
// hardware is used. The zip is checked against the release's SHA256SUMS. Each run owns its HttpClient; Handler
// replaces the network in tests and is borrowed, not disposed.
public sealed partial record FirmwareRelease(string? Version = null, HttpMessageHandler? Handler = null) : IFirmwareSource
{
    public const string Repository = "https://api.github.com/repos/SingUp009/Mouthtrap/releases";
    public static readonly Uri Latest = new($"{Repository}/latest");
    public const int MaxZipBytes = 16 * 1024 * 1024;
    private const int MaxTextBytes = 1024 * 1024;

    public Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant) =>
        Scope<ProvisioningError, ProvisioningEnvironment>.Gen(async scope =>
        {
            if (Version is not null && !VersionText().IsMatch(Version))
                throw new ArgumentException($"'{Version}' is not a release version.", nameof(Version));
            var client = await scope.Acquire(Client(Handler));
            var address = Version is null ? Latest : new Uri($"{Repository}/tags/v{Version}");
            var release = scope.Get(ReadRelease(await scope.Run(Get(client, address, MaxTextBytes, "application/vnd.github+json"))));
            var asset = $"mouthtrap-firmware-{FirmwarePackage.Slug(variant)}-{release.Version}.zip";
            if (!release.Assets.TryGetValue(asset, out var zipAddress) || !release.Assets.TryGetValue("SHA256SUMS", out var sumsAddress))
                throw scope.Fail(new FirmwareInvalid($"Release {release.Version} has no {asset} with SHA256SUMS."));
            var sums = await scope.Run(Get(client, sumsAddress, MaxTextBytes, "application/octet-stream"));
            var expected = scope.Get(Checksum(sums, asset));
            var zip = await scope.Run(Get(client, zipAddress, MaxZipBytes, "application/octet-stream"));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(zip), expected))
                throw scope.Fail(new FirmwareInvalid($"{asset} does not match the checksum in SHA256SUMS."));
            var firmware = scope.Get(FirmwareFile.Read(zip, variant));
            if (firmware.Version != release.Version)
                throw scope.Fail(new FirmwareInvalid($"{asset} says it is version {firmware.Version}, not {release.Version}."));
            return firmware;
        }).OnThreadPool();

    // The published releases, newest first, as GitHub lists them (its first 30).
    public static Effect<IReadOnlyList<ReleaseVersion>, ProvisioningError, ProvisioningEnvironment> Releases(HttpMessageHandler? handler = null) =>
        Scope<ProvisioningError, ProvisioningEnvironment>.Gen(async scope =>
        {
            var client = await scope.Acquire(Client(handler));
            var json = await scope.Run(Get(client, new Uri($"{Repository}?per_page=30"), MaxTextBytes, "application/vnd.github+json"));
            return scope.Get(ReadReleases(json));
        }).OnThreadPool();

    private sealed record Release(string Version, Dictionary<string, Uri> Assets);

    private static Resource<HttpClient, ProvisioningError, ProvisioningEnvironment> Client(HttpMessageHandler? handler) =>
        Resource.Make(Effect<ProvisioningEnvironment>.Sync(() =>
        {
            var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(60);
            // GitHub's API refuses requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mouthtrap-firmware-installer", "1"));
            return client;
        }), (client, _) => Effect<ProvisioningEnvironment>.Sync(() =>
        {
            client.Dispose();
            return Unit.Value;
        }));

    // The body of a successful response of at most limit bytes. A request that fails or times out is unavailable, and
    // so is a status other than success, with its code; the caller's cancellation stays a cancellation.
    private static Effect<byte[], ProvisioningError, ProvisioningEnvironment> Get(HttpClient client, Uri address, int limit, string accept) =>
        Effect<ProvisioningEnvironment>.FromResultAsync<byte[], ProvisioningError>(async cancellationToken =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new(accept));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    return new Err<ProvisioningError>(new FirmwareUnavailable(code != 404 ? $"{address.Host} answered {code} ({response.ReasonPhrase})."
                        : address == Latest ? "No release of Mouthtrap has been published yet."
                        : address.AbsolutePath.Contains("/tags/", StringComparison.Ordinal) ? $"Release {address.Segments[^1]} is not published."
                        : $"{address.Segments[^1]} is missing from the release.", code));
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
                return new Err<ProvisioningError>(new FirmwareUnavailable($"{address.Host} cannot be reached: {exception.Message}", (int?)exception.StatusCode));
            }
            catch (IOException exception)
            {
                return new Err<ProvisioningError>(new FirmwareUnavailable($"The download from {address.Host} broke off: {exception.Message}"));
            }
            // HttpClient reports its own timeout as a cancellation of a token the caller did not cancel.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Err<ProvisioningError>(new FirmwareUnavailable($"{address.Host} did not answer within {client.Timeout.TotalSeconds:0} seconds."));
            }
        });

    // tag_name, without the leading v, and the download URL of each asset.
    private static Result<Release, ProvisioningError> ReadRelease(byte[] json) => Parse(json, root =>
    {
        var version = TagVersion(root);
        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps) continue;
            assets[name] = address;
        }
        return new Release(version, assets);
    });

    private static Result<IReadOnlyList<ReleaseVersion>, ProvisioningError> ReadReleases(byte[] json) => Parse<IReadOnlyList<ReleaseVersion>>(json, root =>
        [.. root.EnumerateArray()
            .Where(release => !(release.TryGetProperty("draft", out var draft) && draft.GetBoolean()))
            .Select(release => new ReleaseVersion(TagVersion(release),
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean(),
                release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                    ? published.GetDateTimeOffset() : null))]);

    private static Result<T, ProvisioningError> Parse<T>(byte[] json, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return new Ok<T>(read(document.RootElement));
        }
        // GitHub answered with something that is not the release JSON it documents.
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new Err<ProvisioningError>(new FirmwareInvalid($"GitHub's release information cannot be read: {exception.Message}"));
        }
    }

    private static string TagVersion(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString();
        return tag is ['v', .. var version] && VersionText().IsMatch(version)
            ? version
            : throw new FormatException($"The release tag {tag} is not v<version>.");
    }

    // The SHA-256 of name in a SHA256SUMS file of "<hex>  <name>" lines, as sha256sum writes them.
    private static Result<byte[], ProvisioningError> Checksum(byte[] sums, string name)
    {
        foreach (var line in Encoding.UTF8.GetString(sums).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || fields[1].TrimStart('*') != name) continue;
            return fields[0].Length == 64 && fields[0].All(char.IsAsciiHexDigit)
                ? new Ok<byte[]>(Convert.FromHexString(fields[0]))
                : new Err<ProvisioningError>(new FirmwareInvalid($"SHA256SUMS has no valid checksum for {name}."));
        }
        return new Err<ProvisioningError>(new FirmwareInvalid($"SHA256SUMS does not list {name}."));
    }

    private static Err<ProvisioningError> TooLarge(Uri address, int limit) =>
        new(new FirmwareInvalid($"{address.Segments[^1]} is larger than {limit / 1024 / 1024} MB."));

    // Letters, digits, dots, hyphens and plus signs, as in 1.2.0 or 1.3.0-rc.1, so that it can sit in a URL path.
    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex VersionText();
}
