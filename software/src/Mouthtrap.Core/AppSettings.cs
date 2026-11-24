using System.Globalization;
using Moderato.Results;

namespace Mouthtrap.Core;

public enum InputSourceKind { Camera, Image, Video, Mjpeg }

// The language of the GUI and of the messages it shows. System follows the Windows display language.
public enum AppLanguage { System, English, Japanese }

public static class AppLanguages
{
    // The UI culture to use, or null to keep the one Windows gives the process.
    public static CultureInfo? Culture(AppLanguage language) => language switch
    {
        AppLanguage.System => null,
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.Japanese => CultureInfo.GetCultureInfo("ja"),
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };
}

// What the GUI and CLI remember between runs. Every part has a default, so a file only needs the parts it changes.
// The stream token itself is never stored: only the path of the file that holds it.
public sealed record AppSettings
{
    public const int CurrentVersion = 1;
    public static AppSettings Default { get; } = new();

    public int Version { get; init; } = CurrentVersion;
    public InputSettings Input { get; init; } = new();
    public ModelSettings Model { get; init; } = new();
    public PreprocessingSettings Preprocessing { get; init; } = new();
    public ExpressionSettings Expressions { get; init; } = new();
    public OscSettings Osc { get; init; } = new();
    public AppLanguage Language { get; init; } = AppLanguage.System;
}

public sealed record InputSettings
{
    public InputSourceKind Kind { get; init; } = InputSourceKind.Camera;
    public CameraSettings? Camera { get; init; }
    public string? ImagePath { get; init; }
    public string? VideoPath { get; init; }
    public string? MjpegUrl { get; init; }
    public string? TokenFilePath { get; init; }
}

// A camera is found again by its identity (the device path), then by name; the mode by its exact characteristics.
public sealed record CameraSettings(string Name, string Identity, CameraModeSettings? Mode = null);
public sealed record CameraModeSettings(int Width, int Height, string PixelFormat, double Fps);

public sealed record ModelSettings
{
    public bool Enabled { get; init; }
    public string? DefinitionPath { get; init; }
}

// Crop in percent of the frame, as typed in the GUI and the CLI. UseCrop keeps the typed values while the full frame is used.
public sealed record PreprocessingSettings
{
    public bool UseCrop { get; init; }
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropWidth { get; init; } = 100;
    public double CropHeight { get; init; } = 100;
    public int Rotation { get; init; }
    public bool Mirror { get; init; }

    public Result<CropRegion, InvalidValue> ToCrop() =>
        UseCrop ? CropRegion.Create(CropLeft / 100, CropTop / 100, CropWidth / 100, CropHeight / 100) : new Ok<CropRegion>(CropRegion.FullFrame);

    public Result<PreprocessSettings, InvalidValue> ToPreprocess() =>
        ToCrop().AndThen(crop => RotationToTurn(Rotation).Map(turn => new PreprocessSettings(crop, turn, Mirror)));

    public static Result<QuarterTurn, InvalidValue> RotationToTurn(int degrees) => degrees switch
    {
        0 => new Ok<QuarterTurn>(QuarterTurn.None),
        90 => new Ok<QuarterTurn>(QuarterTurn.Clockwise90),
        180 => new Ok<QuarterTurn>(QuarterTurn.HalfTurn),
        270 => new Ok<QuarterTurn>(QuarterTurn.Clockwise270),
        _ => new Err<InvalidValue>(new("rotation", "Rotation must be 0, 90, 180 or 270."))
    };

    public static int TurnToRotation(QuarterTurn turn) => turn switch
    {
        QuarterTurn.None => 0,
        QuarterTurn.Clockwise90 => 90,
        QuarterTurn.HalfTurn => 180,
        QuarterTurn.Clockwise270 => 270,
        _ => throw new ArgumentOutOfRangeException(nameof(turn))
    };
}

public sealed record ExpressionSettings
{
    public bool AdaptiveCalibration { get; init; } = ExpressionOptions.Default.AdaptiveCalibration;
    public bool Smoothing { get; init; } = ExpressionOptions.Default.Smoothing;
    public float Deadzone { get; init; } = Core.Deadzone.DefaultValue;
    public bool Tongue { get; init; } = ExpressionOptions.Default.Tongue;

    // Settings that did not come through SettingsStore may hold a deadzone that cannot be used.
    public Result<ExpressionOptions, InvalidValue> ToOptions() => Core.Deadzone.Create(Deadzone).Map(deadzone =>
        new ExpressionOptions { AdaptiveCalibration = AdaptiveCalibration, Smoothing = Smoothing, Deadzone = deadzone, Tongue = Tongue });
}

public sealed record OscSettings
{
    public const string DefaultEndpoint = "127.0.0.1:9000";
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = DefaultEndpoint;
}
