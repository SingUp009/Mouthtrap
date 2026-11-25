using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;

namespace Mouthtrap.Devices.Tests;

public sealed class FirmwareReleaseTests
{
    private const string ZipName = "mouthtrap-firmware-xiao-wifi-1.2.0.zip";
    private static readonly byte[] Zip = Packages.Zip();
    private static readonly Uri Tag = new($"{FirmwareRelease.Repository}/tags/v1.2.0");

    [Fact]
    public async Task Load_LatestRelease_ReturnsTheCheckedFirmwareAndBorrowsTheHandler()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        var firmware = Ok(exit);
        Assert.Equal("1.2.0", firmware.Version);
        Assert.Equal(3, firmware.Plan.Parts.Count);
        Assert.Equal(FirmwareRelease.Latest, github.Requests[0]);
        Assert.Equal(["Mouthtrap-firmware-installer/1"], github.UserAgents.Distinct());
        Assert.False(github.Disposed);
    }

    [Fact]
    public async Task Load_SpecificVersion_AsksForItsTag()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Load(new FirmwareRelease("1.2.0", github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.Equal("1.2.0", Ok(exit).Version);
        Assert.Equal(Tag, github.Requests[0]);
    }

    [Fact]
    public async Task Load_VersionThatIsNotPublished_FailsWithFirmwareUnavailable404()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Load(new FirmwareRelease("9.9.9", github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareUnavailable(var message, 404)) && message.Contains("v9.9.9"), $"Got {exit}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.0/../../other")]
    [InlineData("1.2.0?x=1")]
    public async Task Load_VersionThatCannotBeATag_Dies(string version)
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Load(new FirmwareRelease(version, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Die { Exception: ArgumentException }, $"Got {exit}.");
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Load_ZipThatDoesNotMatchItsChecksum_FailsWithFirmwareInvalid()
    {
        // Given a zip that changed after SHA256SUMS was written
        using var github = new GitHub { Sums = $"{Convert.ToHexStringLower(SHA256.HashData(Packages.Zip(folder: "other")))}  {ZipName}\n" };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("checksum"), $"Got {exit}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123  mouthtrap-firmware-xiao-wifi-1.2.0.zip\n")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  other.zip\n")]
    public async Task Load_SumsWithoutAValidChecksumForTheZip_FailsWithFirmwareInvalid(string sums)
    {
        // Given
        using var github = new GitHub { Sums = sums };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_VariantMissingFromTheRelease_FailsWithFirmwareInvalidBeforeDownloading()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoUvc);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid), $"Got {exit}.");
        Assert.Equal([FirmwareRelease.Latest], github.Requests);
    }

    [Fact]
    public async Task Load_NoPublishedRelease_FailsWithFirmwareUnavailable404()
    {
        // Given
        using var github = new GitHub { LatestStatus = HttpStatusCode.NotFound };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareUnavailable(_, 404)), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_NetworkFails_FailsWithFirmwareUnavailable()
    {
        // Given
        using var github = new GitHub { Fail = true };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareUnavailable), $"Got {exit}.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"assets\": []}")]
    [InlineData("{\"tag_name\": \"1.2.0\", \"assets\": []}")]
    public async Task Load_ReleaseInformationThatCannotBeRead_FailsWithFirmwareInvalid(string json)
    {
        // Given
        using var github = new GitHub { ReleaseJson = json };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_ZipLargerThanTheLimit_FailsWithFirmwareInvalid()
    {
        // Given
        using var github = new GitHub { ZipLength = FirmwareRelease.MaxZipBytes + 1L };

        // When
        var exit = await Load(new FirmwareRelease(null, github), FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("larger"), $"Got {exit}.");
    }

    [Fact]
    public async Task Releases_PublishedAndDraft_ListsThePublishedNewestFirst()
    {
        // Given
        using var github = new GitHub
        {
            ListJson = """
                [{"tag_name": "v1.3.0-rc.1", "prerelease": true, "draft": false, "published_at": "2026-11-20T10:00:00Z"},
                 {"tag_name": "v1.3.0", "prerelease": false, "draft": true, "published_at": null},
                 {"tag_name": "v1.2.0", "prerelease": false, "draft": false, "published_at": "2026-11-01T10:00:00Z"}]
                """
        };

        // When
        var exit = await FirmwareRelease.Releases(github).RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()),
            TestContext.Current.CancellationToken);

        // Then
        if (exit is not Ok<IReadOnlyList<ReleaseVersion>>(var releases)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.Equal([new ReleaseVersion("1.3.0-rc.1", true, new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)),
            new ReleaseVersion("1.2.0", false, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero))], releases);
    }

    [Fact]
    public async Task Releases_TagThatIsNotAVersion_FailsWithFirmwareInvalid()
    {
        // Given
        using var github = new GitHub { ListJson = """[{"tag_name": "nightly", "draft": false}]""" };

        // When
        var exit = await FirmwareRelease.Releases(github).RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()),
            TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid), $"Got {exit}.");
    }

    private static async Task<Exit<LoadedFirmware, ProvisioningError>> Load(FirmwareRelease release, FirmwareVariant variant) =>
        await release.Load(variant).RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken);

    private static LoadedFirmware Ok(Exit<LoadedFirmware, ProvisioningError> exit) =>
        exit is Ok<LoadedFirmware>(var firmware) ? firmware : throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");

    // The GitHub API and release downloads, with one release, v1.2.0, that holds the Wi-Fi firmware and SHA256SUMS.
    private sealed class GitHub : HttpMessageHandler
    {
        private static readonly Uri ZipUrl = new($"https://github.com/SingUp009/Mouthtrap/releases/download/v1.2.0/{ZipName}");
        private static readonly Uri SumsUrl = new("https://github.com/SingUp009/Mouthtrap/releases/download/v1.2.0/SHA256SUMS");

        public string ReleaseJson { get; init; } = $$"""
            {"tag_name": "v1.2.0", "assets": [
              {"name": "{{ZipName}}", "browser_download_url": "{{ZipUrl}}"},
              {"name": "SHA256SUMS", "browser_download_url": "{{SumsUrl}}"}]}
            """;
        public string ListJson { get; init; } = "[]";
        public string Sums { get; init; } = $"{Convert.ToHexStringLower(SHA256.HashData(Zip))}  {ZipName}\n";
        public HttpStatusCode LatestStatus { get; init; } = HttpStatusCode.OK;
        public bool Fail { get; init; }
        public long? ZipLength { get; init; }
        public List<Uri> Requests { get; } = [];
        public List<string> UserAgents { get; } = [];
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            if (Fail) throw new HttpRequestException("No route to host.");
            HttpContent? content = null;
            var status = HttpStatusCode.OK;
            if (request.RequestUri == FirmwareRelease.Latest)
            {
                status = LatestStatus;
                content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json");
            }
            else if (request.RequestUri == Tag) content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json");
            else if (request.RequestUri == new Uri($"{FirmwareRelease.Repository}?per_page=30")) content = new StringContent(ListJson, Encoding.UTF8, "application/json");
            else if (request.RequestUri == SumsUrl) content = new StringContent(Sums);
            else if (request.RequestUri == ZipUrl)
            {
                content = new ByteArrayContent(Zip);
                if (ZipLength is { } length) content.Headers.ContentLength = length;
            }
            else status = HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content ?? new StringContent("") });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
