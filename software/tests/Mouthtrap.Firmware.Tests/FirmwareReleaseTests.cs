using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Firmware;

namespace Mouthtrap.Firmware.Tests;

public sealed class FirmwareReleaseTests
{
    private const string ZipName = "mouthtrap-firmware-xiao-wifi-1.2.0.zip";
    private static readonly byte[] Zip = Packages.Zip();

    [Fact]
    public async Task Download_LatestRelease_ReturnsTheCheckedFirmwareAndBorrowsTheHandler()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        if (exit is not Ok<FirmwareDownload>(var download)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.Equal("1.2.0", download.Version);
        Assert.Equal(FirmwareVariant.XiaoWifi, download.Variant);
        Assert.Equal(3, download.Plan.Parts.Count);
        Assert.Equal(["Mouthtrap-firmware-installer/1"], github.UserAgents.Distinct());
        Assert.False(github.Disposed);
    }

    [Fact]
    public async Task Download_ZipThatDoesNotMatchItsChecksum_FailsWithReleaseInvalid()
    {
        // Given a zip that changed after SHA256SUMS was written
        using var github = new GitHub { Sums = $"{Convert.ToHexStringLower(SHA256.HashData(Packages.Zip(folder: "other")))}  {ZipName}\n" };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseInvalid { Message: var message }) && message.Contains("checksum"), $"Got {exit}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123  mouthtrap-firmware-xiao-wifi-1.2.0.zip\n")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  other.zip\n")]
    public async Task Download_SumsWithoutAValidChecksumForTheZip_FailsWithReleaseInvalid(string sums)
    {
        // Given
        using var github = new GitHub { Sums = sums };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseInvalid), $"Got {exit}.");
    }

    [Fact]
    public async Task Download_VariantMissingFromTheRelease_FailsWithReleaseInvalidBeforeDownloading()
    {
        // Given
        using var github = new GitHub();

        // When
        var exit = await Download(github, FirmwareVariant.XiaoUvc);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseInvalid), $"Got {exit}.");
        Assert.Equal([FirmwareRelease.Latest], github.Requests);
    }

    [Fact]
    public async Task Download_NoPublishedRelease_FailsWithReleaseUnavailable404()
    {
        // Given
        using var github = new GitHub { LatestStatus = HttpStatusCode.NotFound };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseUnavailable(_, 404)), $"Got {exit}.");
    }

    [Fact]
    public async Task Download_NetworkFails_FailsWithReleaseUnavailable()
    {
        // Given
        using var github = new GitHub { Fail = true };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseUnavailable), $"Got {exit}.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"assets\": []}")]
    [InlineData("{\"tag_name\": \"1.2.0\", \"assets\": []}")]
    public async Task Download_ReleaseInformationThatCannotBeRead_FailsWithReleaseInvalid(string json)
    {
        // Given
        using var github = new GitHub { ReleaseJson = json };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseInvalid), $"Got {exit}.");
    }

    [Fact]
    public async Task Download_ZipLargerThanTheLimit_FailsWithReleaseInvalid()
    {
        // Given
        using var github = new GitHub { ZipLength = FirmwareRelease.MaxZipBytes + 1L };

        // When
        var exit = await Download(github, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ReleaseError>(ReleaseInvalid { Message: var message }) && message.Contains("larger"), $"Got {exit}.");
    }

    private static async Task<Exit<FirmwareDownload, ReleaseError>> Download(GitHub github, FirmwareVariant variant) =>
        await new FirmwareRelease(github).Download(variant)
            .RunExitAsync(new FirmwareEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken);

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
