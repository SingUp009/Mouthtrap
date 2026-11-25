using System.Globalization;
using System.IO.Compression;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// The firmware variants a release ships, named as in the zip files that firmware/build-release.sh writes.
public enum FirmwareVariant { XiaoWifi, XiaoUvc, XiaoUvcMultimode }

// What manifest.json in a release zip says the package is for, as firmware/build-release.sh writes it.
public sealed record FirmwareManifest(string Board, string Firmware, string Version)
{
    public const int Format = 1;
    public const string XiaoEsp32S3Sense = "seeed-xiao-esp32s3-sense";
}

// A release zip: its manifest and what it writes.
public sealed record FirmwareArchive(FirmwareManifest Manifest, FlashPlan Plan);

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

    // The application image of a variant, as flash_args names it.
    public static string AppImage(FirmwareVariant variant) => variant switch
    {
        FirmwareVariant.XiaoWifi => "mouthtrap_xiao_wifi.bin",
        FirmwareVariant.XiaoUvc or FirmwareVariant.XiaoUvcMultimode => "mouthtrap_xiao_uvc.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };

    public static Result<FirmwareArchive, InvalidValue> FromZip(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var flashArgs = archive.Entries.Where(entry => entry.FullName.EndsWith("flash_args", StringComparison.Ordinal) &&
                entry.FullName.Count(value => value == '/') <= 1).ToArray();
            if (flashArgs.Length != 1) return Invalid("The zip file must hold one flash_args file in its top-level folder.");
            var root = flashArgs[0].FullName[..^"flash_args".Length];
            byte[]? Read(string path) => archive.GetEntry(root + path) is { } entry ? ReadLimited(entry.Open(), entry.Length) : null;
            if (Read("manifest.json") is not { } json) return Invalid("The zip has no manifest.json, so what it is for cannot be checked.");
            var manifest = ReadManifest(json);
            if (!manifest.TryGetOk(out var parsed)) return new Err<InvalidValue>(manifest.TryGetErr(out var manifestError) ? manifestError! : null!);
            var plan = Parse(Read);
            if (!plan.TryGetOk(out var value)) return new Err<InvalidValue>(plan.TryGetErr(out var error) ? error! : null!);
            return new Ok<FirmwareArchive>(new(parsed!, value!));
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

    private static Result<FirmwareManifest, InvalidValue> ReadManifest(byte[] json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != FirmwareManifest.Format)
                return Invalid($"manifest.json has format {root.GetProperty("format")}; this Mouthtrap reads format {FirmwareManifest.Format}.");
            var board = root.GetProperty("board").GetString();
            var firmware = root.GetProperty("firmware").GetString();
            var version = root.GetProperty("version").GetString();
            return string.IsNullOrEmpty(board) || string.IsNullOrEmpty(firmware) || string.IsNullOrEmpty(version)
                ? Invalid("manifest.json must give the board, the firmware and the version.")
                : new Ok<FirmwareManifest>(new(board, firmware, version));
        }
        // A manifest that is not the JSON object build-release.sh writes.
        catch (Exception exception) when (exception is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Invalid($"manifest.json cannot be read: {exception.Message}");
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
