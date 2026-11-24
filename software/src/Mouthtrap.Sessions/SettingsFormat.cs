using System.Text.Json;
using System.Text.Json.Serialization;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Sessions;

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

// The settings JSON format: parsing, validation and serialization, without touching files. Where the file lives and
// how it is read and replaced belongs to SettingsStore.
public static class SettingsFormat
{
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

    // The settings as indented JSON, once they pass Validate.
    public static Result<byte[], SettingsError> Serialize(AppSettings settings) =>
        Validate(settings).Map(valid => JsonSerializer.SerializeToUtf8Bytes(valid, Options));

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
            if (StreamUrl.Create(url) is Err<InvalidValue>(var invalid)) return new("input.mjpegUrl", invalid.Message);
        }

        if (FilePath(settings.Model.DefinitionPath, "model.definitionPath") is { } model) return model;

        var preprocessing = settings.Preprocessing;
        if (CropRegion.Create(preprocessing.CropLeft / 100, preprocessing.CropTop / 100, preprocessing.CropWidth / 100,
                preprocessing.CropHeight / 100) is Err<InvalidValue>)
            return new("preprocessing.crop", "The crop must lie within 0 to 100 % with a width and height above 0.");
        if (preprocessing.Rotation is not (0 or 90 or 180 or 270)) return new("preprocessing.rotation", "Rotation must be 0, 90, 180 or 270.");

        var expressions = settings.Expressions;
        if (Deadzone.Create(expressions.Deadzone) is Err<InvalidValue>(var deadzone)) return new("expressions.deadzone", deadzone.Message);

        if (OscEndpoint.Create(settings.Osc.Endpoint) is Err<InvalidValue>(var endpoint)) return new("osc.endpoint", endpoint.Message);
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
