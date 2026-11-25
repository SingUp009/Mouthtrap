using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// Firmware ready to write. Manifest is what a release zip says about itself; a build folder has none.
public sealed record LoadedFirmware(string Version, FlashPlan Plan)
{
    public FirmwareManifest? Manifest { get; init; }
}

// Where the firmware to write comes from: a release on GitHub (FirmwareRelease) or a zip file (FirmwareFile). Both
// refuse a zip whose manifest names another board or variant than the one asked for.
public interface IFirmwareSource
{
    Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant);
}

// A release zip on disk, such as one downloaded from a draft release to check it on hardware before publishing.
public sealed record FirmwareFile(string Path) : IFirmwareSource
{
    public Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant) =>
        Effect<ProvisioningEnvironment>.FromResultAsync<LoadedFirmware, ProvisioningError>(async cancellationToken =>
        {
            byte[] zip;
            try
            {
                if (new FileInfo(Path).Length > FirmwareRelease.MaxZipBytes)
                    return new Err<ProvisioningError>(new FirmwareInvalid($"{Path} is larger than {FirmwareRelease.MaxZipBytes / 1024 / 1024} MB."));
                zip = await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false);
            }
            // Opening or reading the file: missing, access denied, in use, or a path that is not valid.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new Err<ProvisioningError>(new FirmwareUnavailable($"{Path} cannot be read: {exception.Message}"));
            }
            return Read(zip, variant);
        });

    // The plan in a release zip whose manifest names the XIAO ESP32S3 Sense and variant. The application image that
    // flash_args lists is checked as well. Version is the manifest's.
    internal static Result<LoadedFirmware, ProvisioningError> Read(byte[] zip)
    {
        using var stream = new MemoryStream(zip, writable: false);
        var parsed = FirmwarePackage.FromZip(stream);
        if (!parsed.TryGetOk(out var archive))
            return new Err<ProvisioningError>(new FirmwareInvalid(parsed.TryGetErr(out var invalid) ? invalid!.Message : ""));
        return new Ok<LoadedFirmware>(new(archive!.Manifest.Version, archive.Plan) { Manifest = archive.Manifest });
    }

    internal static Result<LoadedFirmware, ProvisioningError> Read(byte[] zip, FirmwareVariant variant)
    {
        var read = Read(zip);
        if (!read.TryGetOk(out var firmware)) return read;
        var manifest = firmware!.Manifest!;
        if (manifest.Board != FirmwareManifest.XiaoEsp32S3Sense)
            return new Err<ProvisioningError>(new FirmwareInvalid($"The zip is for the board {manifest.Board}, not the XIAO ESP32S3 Sense."));
        if (manifest.Firmware != FirmwarePackage.Slug(variant))
            return new Err<ProvisioningError>(new FirmwareInvalid($"The zip holds the {manifest.Firmware} firmware, not {FirmwarePackage.Slug(variant)}."));
        var app = FirmwarePackage.AppImage(variant);
        if (!firmware.Plan.Parts.Any(part => part.Name == app))
            return new Err<ProvisioningError>(new FirmwareInvalid($"The zip has no {app}, so it is not the {FirmwarePackage.Slug(variant)} firmware."));
        return read;
    }
}
