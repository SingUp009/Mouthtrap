using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;
using Mouthtrap.Tests;

namespace Mouthtrap.Devices.Tests;

public sealed class FirmwarePackageTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-package-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void FromZip_ReleaseZip_PlansEveryImageAtItsOffset()
    {
        // Given
        using var zip = new MemoryStream(Packages.Zip());

        // When
        var archive = ResultAssert.Ok(FirmwarePackage.FromZip(zip));

        // Then
        var plan = archive.Plan;
        Assert.Equal(new FirmwareManifest(FirmwareManifest.XiaoEsp32S3Sense, "xiao-wifi", "1.2.0"), archive.Manifest);
        Assert.Equal((uint)(8 * 1024 * 1024), plan.FlashSize);
        Assert.Equal([0x0u, 0x8000u, 0x10000u], plan.Parts.Select(part => part.Offset));
        Assert.Equal(Packages.App, plan.Parts[2].Data.ToArray());
        Assert.Equal("mouthtrap_xiao_wifi.bin", plan.Parts[2].Name);
    }

    [Fact]
    public void FromDirectory_BuildFolder_PlansTheSameImages()
    {
        // Given
        File.WriteAllText(Path.Combine(directory, "flash_args"), Packages.FlashArgs);
        Directory.CreateDirectory(Path.Combine(directory, "bootloader"));
        Directory.CreateDirectory(Path.Combine(directory, "partition_table"));
        File.WriteAllBytes(Path.Combine(directory, "bootloader", "bootloader.bin"), Packages.Bootloader);
        File.WriteAllBytes(Path.Combine(directory, "partition_table", "partition-table.bin"), Packages.PartitionTable);
        File.WriteAllBytes(Path.Combine(directory, "mouthtrap_xiao_wifi.bin"), Packages.App);

        // When
        var plan = ResultAssert.Ok(FirmwarePackage.FromDirectory(directory));

        // Then
        Assert.Equal(3, plan.Parts.Count);
        Assert.Equal(Packages.Bootloader, plan.Parts[0].Data.ToArray());
    }

    [Theory]
    [InlineData("--flash_mode dio --flash_freq 80m\n0x0 bootloader/bootloader.bin\n")] // no flash size
    [InlineData("--flash_size 8MB\n")] // no image
    [InlineData("--flash_size 8MB\n0x0\n")] // no file
    [InlineData("--flash_size 8MB\n0 bootloader/bootloader.bin\n")] // offset not in hexadecimal
    [InlineData("--flash_size 8MB\n0x0 ../bootloader.bin\n")] // outside the package
    [InlineData("--flash_size 8MB\n0x0 /etc/passwd\n")] // rooted path
    [InlineData("--flash_size 8MB\n0x0 missing.bin\n")] // missing file
    public void FromZip_FlashArgsThatCannotBeUsed_ReturnsInvalidValue(string flashArgs)
    {
        // Given
        using var zip = new MemoryStream(Packages.Zip(flashArgs: flashArgs));

        // When / Then
        Assert.Equal(nameof(FirmwarePackage), ResultAssert.Err(FirmwarePackage.FromZip(zip)).Name);
    }

    [Fact]
    public void FromZip_NotAZipFile_ReturnsInvalidValue()
    {
        // Given
        using var zip = new MemoryStream("not a zip"u8.ToArray());

        // When / Then
        Assert.Contains("cannot be read", ResultAssert.Err(FirmwarePackage.FromZip(zip)).Message);
    }

    [Fact]
    public void FromZip_ZipWithoutFlashArgs_ReturnsInvalidValue()
    {
        // Given
        using var zip = new MemoryStream(Packages.Zip(folder: "a/b"));

        // When / Then
        Assert.Contains("flash_args", ResultAssert.Err(FirmwarePackage.FromZip(zip)).Message);
    }

    [Fact]
    public void FromZip_ImageForAnotherChip_ReturnsInvalidValue()
    {
        // Given an ESP32 application image
        using var zip = new MemoryStream(Packages.Zip(files: [("bootloader/bootloader.bin", Packages.Bootloader),
            ("mouthtrap_xiao_wifi.bin", Packages.Image(9000, chip: 0)), ("partition_table/partition-table.bin", Packages.PartitionTable)]));

        // When / Then
        Assert.Contains("not an ESP32-S3 image", ResultAssert.Err(FirmwarePackage.FromZip(zip)).Message);
    }

    [Theory]
    [InlineData(FirmwareVariant.XiaoWifi, "xiao-wifi")]
    [InlineData(FirmwareVariant.XiaoUvc, "xiao-uvc")]
    [InlineData(FirmwareVariant.XiaoUvcMultimode, "xiao-uvc-multimode")]
    public void Slug_Variant_IsTheNameInTheReleaseZip(FirmwareVariant variant, string slug)
    {
        // When / Then
        Assert.Equal(slug, FirmwarePackage.Slug(variant));
    }

    [Theory]
    [InlineData(FirmwareVariant.XiaoWifi, "mouthtrap_xiao_wifi.bin")]
    [InlineData(FirmwareVariant.XiaoUvc, "mouthtrap_xiao_uvc.bin")]
    [InlineData(FirmwareVariant.XiaoUvcMultimode, "mouthtrap_xiao_uvc.bin")]
    public void AppImage_Variant_IsTheFileFlashArgsNames(FirmwareVariant variant, string file)
    {
        // When / Then
        Assert.Equal(file, FirmwarePackage.AppImage(variant));
    }

    [Fact]
    public void Slug_UnknownVariant_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => FirmwarePackage.Slug((FirmwareVariant)99));
    }
}
