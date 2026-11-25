using System.Text.RegularExpressions;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// Firmware ready to write: Version is the release version, or the zip file's name for a build that names none.
public sealed record LoadedFirmware(string Version, FlashPlan Plan);

// Where the firmware to write comes from: a release on GitHub (FirmwareRelease) or a zip file (FirmwareFile). Each
// checks what it loads as far as it can, and refuses a zip that holds another variant than the one asked for.
public interface IFirmwareSource
{
    Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant);
}

// A release zip on disk, such as one downloaded from a draft release to check it on hardware before publishing.
public sealed partial record FirmwareFile(string Path) : IFirmwareSource
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
            return Read(zip, variant, System.IO.Path.GetFileNameWithoutExtension(Path));
        });

    // The plan in a release zip, checked to hold variant. The version comes from the zip's folder, as
    // firmware/build-release.sh names it, or is fallback when the folder names none.
    internal static Result<LoadedFirmware, ProvisioningError> Read(byte[] zip, FirmwareVariant variant, string fallback)
    {
        using var stream = new MemoryStream(zip, writable: false);
        var parsed = FirmwarePackage.FromZip(stream);
        if (!parsed.TryGetOk(out var archive))
            return new Err<ProvisioningError>(new FirmwareInvalid(parsed.TryGetErr(out var invalid) ? invalid!.Message : ""));
        var version = fallback;
        if (ReleaseFolder().Match(archive!.Folder) is { Success: true } folder)
        {
            if (folder.Groups[1].Value != FirmwarePackage.Slug(variant))
                return new Err<ProvisioningError>(new FirmwareInvalid($"The zip holds the {folder.Groups[1].Value} firmware, not {FirmwarePackage.Slug(variant)}."));
            version = folder.Groups[2].Value;
        }
        var app = FirmwarePackage.AppImage(variant);
        if (!archive.Plan.Parts.Any(part => part.Name == app))
            return new Err<ProvisioningError>(new FirmwareInvalid($"The zip has no {app}, so it is not the {FirmwarePackage.Slug(variant)} firmware."));
        return new Ok<LoadedFirmware>(new(version, archive.Plan));
    }

    [GeneratedRegex(@"^mouthtrap-firmware-(xiao-wifi|xiao-uvc-multimode|xiao-uvc)-(.+)$")]
    private static partial Regex ReleaseFolder();
}
