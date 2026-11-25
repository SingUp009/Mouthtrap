using System.Globalization;
using System.IO.Compression;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Firmware;

// The firmware variants a release ships, named as in the zip files that firmware/build-release.sh writes.
public enum FirmwareVariant { XiaoWifi, XiaoUvc, XiaoUvcMultimode }

// A firmware build as ESP-IDF describes it in flash_args: the flash options on the first line, then one
// "<offset> <file>" line per image, with paths relative to flash_args. Read from a release zip, whose files sit in one
// top-level folder, or from a build folder.
public static class FirmwarePackage
{
    // The largest file read from a package: larger than any 8 MB flash image.
    public const int MaxFileBytes = 16 * 1024 * 1024;

    public static string Slug(FirmwareVariant variant) => variant switch
    {
        FirmwareVariant.XiaoWifi => "xiao-wifi",
        FirmwareVariant.XiaoUvc => "xiao-uvc",
        FirmwareVariant.XiaoUvcMultimode => "xiao-uvc-multimode",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };

    public static Result<FlashPlan, InvalidValue> FromZip(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var flashArgs = archive.Entries.Where(entry => entry.FullName.EndsWith("flash_args", StringComparison.Ordinal) &&
                entry.FullName.Count(value => value == '/') <= 1).ToArray();
            if (flashArgs.Length != 1) return Invalid("The zip file must hold one flash_args file in its top-level folder.");
            var root = flashArgs[0].FullName[..^"flash_args".Length];
            return Parse(path => archive.GetEntry(root + path) is { } entry ? ReadLimited(entry.Open(), entry.Length) : null);
        }
        // Reading the archive: not a zip file, damaged entries, or a stream that fails.
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Invalid($"The zip file cannot be read: {exception.Message}");
        }
    }

    public static Result<FlashPlan, InvalidValue> FromDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Parse(name => Path.Combine(path, name) is var file && File.Exists(file)
                ? ReadLimited(File.OpenRead(file), new FileInfo(file).Length)
                : null);
        }
        // Reading the files: missing, denied, in use, or larger than any image.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Invalid($"The build folder cannot be read: {exception.Message}");
        }
    }

    // A null file is a missing one.
    private static Result<FlashPlan, InvalidValue> Parse(Func<string, byte[]?> read)
    {
        if (read("flash_args") is not { } arguments) return Invalid("flash_args is missing.");
        var lines = System.Text.Encoding.UTF8.GetString(arguments).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return Invalid("flash_args must list the flash options and at least one image.");
        var flashSize = FlashSize(lines[0]);
        if (flashSize is null) return Invalid("flash_args must give --flash_size in MB, such as 8MB.");
        var parts = new List<FlashPart>();
        foreach (var line in lines[1..])
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !fields[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(fields[0][2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset))
                return Invalid($"This flash_args line is not \"<offset> <file>\": {line}");
            var file = fields[1];
            if (Path.IsPathRooted(file) || file.Split('/', '\\').Any(segment => segment is ".." or ""))
                return Invalid($"flash_args names a file outside the package: {file}");
            if (read(file) is not { } data) return Invalid($"{file} is missing.");
            parts.Add(new(offset, data, file));
        }
        return FlashPlan.Create(parts, flashSize.Value);
    }

    // "--flash_mode dio --flash_freq 80m --flash_size 8MB" gives 8 MiB. The mode and frequency are already in the
    // bootloader's header, which esptool would leave as it is for these values, so the images are written unchanged.
    private static uint? FlashSize(string options)
    {
        var fields = options.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(fields, "--flash_size");
        if (index < 0 || index + 1 >= fields.Length || !fields[index + 1].EndsWith("MB", StringComparison.Ordinal)) return null;
        return uint.TryParse(fields[index + 1][..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var megabytes) && megabytes is >= 1 and <= 128
            ? megabytes * 1024 * 1024
            : null;
    }

    private static byte[]? ReadLimited(Stream stream, long length)
    {
        using (stream)
        {
            if (length > MaxFileBytes) throw new InvalidDataException($"A file in the package is larger than {MaxFileBytes / 1024 / 1024} MB.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    private static Err<InvalidValue> Invalid(string message) => new(new(nameof(FirmwarePackage), message));
}
