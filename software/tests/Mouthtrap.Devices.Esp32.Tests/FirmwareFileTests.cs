using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;

namespace Mouthtrap.Devices.Tests;

public sealed class FirmwareFileTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-zip-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Load_ReleaseZip_TakesTheVersionFromItsFolder()
    {
        // Given
        var path = Save("download.zip", Packages.Zip());

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        if (exit is not Ok<LoadedFirmware>(var firmware)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.Equal("1.2.0", firmware.Version);
        Assert.Equal(3, firmware.Plan.Parts.Count);
    }

    [Fact]
    public async Task Load_ZipOfAnotherBuild_TakesTheVersionFromTheFileName()
    {
        // Given
        var path = Save("my-build.zip", Packages.Zip(folder: "build"));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Ok<LoadedFirmware>({ Version: "my-build" }), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_ZipOfAnotherVariant_FailsWithFirmwareInvalid()
    {
        // Given the USB webcam release, chosen for the Wi-Fi camera
        var path = Save("webcam.zip", Packages.Zip(folder: "mouthtrap-firmware-xiao-uvc-1.2.0"));

        // When
        var exit = await Load(path, FirmwareVariant.XiaoWifi);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareInvalid { Message: var message }) && message.Contains("xiao-uvc"), $"Got {exit}.");
    }

    [Fact]
    public async Task Load_ZipWithoutTheVariantsApp_FailsWithFirmwareInvalid()
    {
        // Given a build of the Wi-Fi firmware, chosen for the USB webcam
        var path = Save("build.zip", Packages.Zip(folder: "build"));

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
        await new FirmwareFile(path).Load(variant).RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken);
}
