using System.Text.Json;
using System.Text.Json.Serialization;
using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Core;

// No settings file exists yet; the caller decides whether that means the defaults.
public sealed record SettingsFileMissing(string Path);
// The file exists but cannot be read (locked, denied, too large).
public sealed record SettingsFileUnreadable(string Path, string Message);
// The file is not settings JSON: malformed, unknown or missing members, or wrong types.
public sealed record SettingsFileCorrupt(string Path, string Message);
// Written by a newer or unknown format version.
public sealed record SettingsVersionUnsupported(string Path, int Version);
// Well-formed, but a value is out of range; Field is the JSON path, such as "preprocessing.cropWidth".
public sealed record SettingsValueInvalid(string Field, string Message);
// The file could not be replaced; the previous file is left as it was.
public sealed record SettingsWriteFailed(string Path, string Message);

[Error]
public partial union SettingsError(SettingsFileMissing, SettingsFileUnreadable, SettingsFileCorrupt,
    SettingsVersionUnsupported, SettingsValueInvalid, SettingsWriteFailed);

public static class SettingsStore
{
    public const int MaxFileBytes = 64 * 1024;
    private const int MaxPathLength = 4096;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    // %APPDATA%\Mouthtrap\settings.json, shared by the GUI and the CLI's --saved-settings.
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mouthtrap", "settings.json");

    public static Result<AppSettings, SettingsError> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxFileBytes)
                return Fail(new SettingsFileUnreadable(path, $"The settings file is larger than {MaxFileBytes} bytes."));
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return Fail(new SettingsFileMissing(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Fail(new SettingsFileUnreadable(path, exception.Message)); }
        return Parse(path, bytes);
    }

    // Path names the source in errors only.
    public static Result<AppSettings, SettingsError> Parse(string path, ReadOnlySpan<byte> json)
    {
        AppSettings? settings;
        try
        {
            // The version is read first, so a newer file is reported as such rather than as unknown members.
            using (var document = JsonDocument.Parse(json.ToArray()))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Fail(new SettingsFileCorrupt(path, "The settings must be a JSON object."));
                if (!document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var number))
                    return Fail(new SettingsFileCorrupt(path, "The settings need an integer \"version\"."));
                if (number != AppSettings.CurrentVersion) return Fail(new SettingsVersionUnsupported(path, number));
            }
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch (JsonException exception) { return Fail(new SettingsFileCorrupt(path, exception.Message)); }
        if (settings is null) return Fail(new SettingsFileCorrupt(path, "The settings must be a JSON object."));
        return Validate(settings);
    }

    public static Result<AppSettings, SettingsError> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Check(settings) is { } invalid) return Fail(invalid);
        return new Ok<AppSettings>(settings);
    }

    // Writes a temporary file next to the target and moves it over the target, so a crash or a full disk leaves
    // either the old file or the new one, never a partial file.
    public static Result<Unit, SettingsError> Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Validate(settings) is Err<SettingsError>(var invalid)) return new Err<SettingsError>(invalid);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        string? temporary = null;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
            temporary = null;
            return new Ok<Unit>(Unit.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return new Err<SettingsError>(new SettingsWriteFailed(path, exception.Message)); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Err<SettingsError> Fail(SettingsError error) => new(error);

    private static SettingsValueInvalid? Check(AppSettings settings)
    {
        if (settings.Version != AppSettings.CurrentVersion) return new("version", $"Only version {AppSettings.CurrentVersion} is supported.");

        if (!Enum.IsDefined(settings.Language)) return new("language", "Unknown language.");

        var input = settings.Input;
        if (!Enum.IsDefined(input.Kind)) return new("input.kind", "Unknown input kind.");
        if (input.Camera is { } camera)
        {
            if (Text(camera.Name, "input.camera.name") is { } name) return name;
            if (Text(camera.Identity, "input.camera.identity") is { } identity) return identity;
            if (camera.Mode is { } mode)
            {
                if (mode.Width is <= 0 or > 16384 || mode.Height is <= 0 or > 16384)
                    return new("input.camera.mode", "The mode size must be 1 to 16384 pixels.");
                if (Text(mode.PixelFormat, "input.camera.mode.pixelFormat") is { } format) return format;
                if (!double.IsFinite(mode.Fps) || mode.Fps <= 0 || mode.Fps > 1000)
                    return new("input.camera.mode.fps", "The frame rate must be above 0 and at most 1000.");
            }
        }
        if (FilePath(input.ImagePath, "input.imagePath") is { } image) return image;
        if (FilePath(input.VideoPath, "input.videoPath") is { } video) return video;
        if (FilePath(input.TokenFilePath, "input.tokenFilePath") is { } token) return token;
        if (input.MjpegUrl is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                return new("input.mjpegUrl", "The stream URL must be an absolute HTTP(S) URL.");
            // Credentials belong in the token file, not in a settings file that may be shared.
            if (address.UserInfo.Length > 0) return new("input.mjpegUrl", "The stream URL must not contain a user name or password.");
        }

        if (FilePath(settings.Model.DefinitionPath, "model.definitionPath") is { } model) return model;

        var preprocessing = settings.Preprocessing;
        if (CropRegion.Create(preprocessing.CropLeft / 100, preprocessing.CropTop / 100, preprocessing.CropWidth / 100,
                preprocessing.CropHeight / 100) is Err<InvalidValue>)
            return new("preprocessing.crop", "The crop must lie within 0 to 100 % with a width and height above 0.");
        if (preprocessing.Rotation is not (0 or 90 or 180 or 270)) return new("preprocessing.rotation", "Rotation must be 0, 90, 180 or 270.");

        var expressions = settings.Expressions;
        if (!float.IsFinite(expressions.Deadzone) || expressions.Deadzone is < 0f or >= .5f)
            return new("expressions.deadzone", "The deadzone must be at least 0 and below 0.5.");

        if (!OscEndpoints.TryParse(settings.Osc.Endpoint, out _))
            return new("osc.endpoint", "The OSC target must be an IP address and port, such as 127.0.0.1:9000.");
        return null;
    }

    private static SettingsValueInvalid? Text(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 1024 ? new(field, "A nonempty value of at most 1024 characters is required.") : null;

    private static SettingsValueInvalid? FilePath(string? value, string field) =>
        value is null ? null
        : value.Length is 0 or > MaxPathLength || !Path.IsPathFullyQualified(value) || value.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0
            ? new(field, "A path must be absolute.")
            : null;
}
