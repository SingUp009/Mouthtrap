using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;

namespace Mouthtrap.Devices.Tests;

public sealed class FirmwareFileTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-zip-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Load_ReleaseZip_TakesTheVersionFromItsManifest()
    {
        // Given a release zip saved under another name
        var path = Save("download.zip", Packages.Zip(manifest: Packages.Manifest(version: "1.2.1")));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        if (exit is not Ok<LoadedFirmware>(var firmware)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.Equal("1.2.1", firmware.Version);
        Assert.Equal(3, firmware.Plan.Parts.Count);
    }

    [Fact]
    public async Task Load_MultimodeWebcamChosenAsTheWebcam_FailsWithFirmwareInvalid()
    {
        // Given the multi-mode webcam renamed to look like the 320x240 one: both have mouthtrap_xiao_uvc.bin
        var path = Save("mouthtrap-firmware-xiao-uvc-1.2.0.zip", Packages.Webcam("xiao-uvc-multimode"));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoUvc);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("xiao-uvc-multimode"), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_ZipWithoutAManifest_FailsWithFirmwareInvalid()
    {
        // Given a zip that says nothing about what it is for
        var path = Save("build.zip", Packages.Zip(folder: "build", manifest: null));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("manifest.json"), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_ManifestForAnotherBoard_FailsWithFirmwareInvalid()
    {
        // Given
        var path = Save("other.zip", Packages.Zip(manifest: Packages.Manifest(board: "esp32-s3-devkitc-1")));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("esp32-s3-devkitc-1"), $"Got {exit}.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format": 2, "board": "seeed-xiao-esp32s3-sense", "firmware": "xiao-wifi", "version": "1.2.0"}""")]
    [InlineData("""{"format": 1, "board": "seeed-xiao-esp32s3-sense", "firmware": "xiao-wifi"}""")]
    [InlineData("""{"format": 1, "board": "", "firmware": "xiao-wifi", "version": "1.2.0"}""")]
    public async Task Load_ManifestThatCannotBeRead_FailsWithFirmwareInvalid(string manifest)
    {
        // Given
        var path = Save("odd.zip", Packages.Zip(manifest: manifest));

        // When / Then
        Assert.True(await Load(path, FirmwareVariant.XiaoWifi) is Err<ProvisioningError>(FirmwareInvalid));
    }

    [Fact]
    public async Task Load_ManifestNamingTheVariantWithoutItsApp_FailsWithFirmwareInvalid()
    {
        // Given the Wi-Fi firmware's images under a webcam manifest
        var path = Save("mixed.zip", Packages.Zip(manifest: Packages.Manifest(firmware: "xiao-uvc")));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoUvc);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("mouthtrap_xiao_uvc.bin"), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_NotAZip_FailsWithFirmwareInvalid()
    {
        // Given
        var path = Save("notes.zip", "not a zip"u8.ToArray());

        // When / Then
        Assert.True(await Load(path, FirmwareVariant.XiaoWifi) is Err<ProvisioningError>(FirmwareInvalid));
    }

    [Fact]
    public async Task Load_MissingFile_FailsWithFirmwareUnavailable()
    {
        // When / Then
        Assert.True(await Load(Path.Combine(directory, "missing.zip"), FirmwareVariant.XiaoWifi) is Err<ProvisioningError>(FirmwareUnavailable));
    }

    private string Save(string name, byte[] zip)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, zip);
        return path;
    }

    private static async Task<Exit<LoadedFirmware, ProvisioningError>> Load(string path, FirmwareVariant variant) =>
        await new FirmwareFile(path).Load(variant).RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
}
