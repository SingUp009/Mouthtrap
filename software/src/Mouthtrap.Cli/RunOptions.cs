using Mouthtrap.Core;

namespace Mouthtrap.Cli;

// The run command's options as given: null (or false for a flag) means not given on the command line.
public sealed record RunArguments
{
    public InputKind? Source { get; init; }
    public string? Path { get; init; }
    public string? Url { get; init; }
    public string? TokenFile { get; init; }
    public string? Model { get; init; }
    public double? CropLeft { get; init; }
    public double? CropTop { get; init; }
    public double? CropWidth { get; init; }
    public double? CropHeight { get; init; }
    public int? Rotation { get; init; }
    public bool Mirror { get; init; }
    public CalibrationMode? Calibration { get; init; }
    public SmoothingMode? Smoothing { get; init; }
    public bool Tongue { get; init; }
    public float? Deadzone { get; init; }
    public string? Osc { get; init; }
    public bool OscDryRun { get; init; }
}

// What the run uses. Camera is the saved camera, found again by identity when no --camera is given.
public sealed record RunOptions(InputKind Source, string? Path, string? Url, string? TokenFile, string? Model,
    double CropLeft, double CropTop, double CropWidth, double CropHeight, int Rotation, bool Mirror,
    CalibrationMode Calibration, SmoothingMode Smoothing, bool Tongue, float Deadzone, string? Osc, bool OscDryRun,
    CameraSettings? Camera)
{
    // Explicit options win; saved settings fill the rest; then the built-in defaults. Saved values that belong to a
    // different input than the one used (such as the saved URL when --source Video is given) are not used. A saved
    // crop is used only when it was turned on, and saved OSC output only when a model is used and no OSC option is given.
    // Flags can only turn on: a saved mirror or tongue cannot be turned off from the command line.
    public static RunOptions Resolve(RunArguments given, AppSettings? saved)
    {
        ArgumentNullException.ThrowIfNull(given);
        var source = given.Source ?? (InputKind)(saved ?? AppSettings.Default).Input.Kind;
        var input = saved?.Input;
        var path = given.Path ?? source switch
        {
            InputKind.Image => input?.ImagePath,
            InputKind.Video => input?.VideoPath,
            _ => null
        };
        var mjpeg = source == InputKind.Mjpeg;
        var url = given.Url ?? (mjpeg ? input?.MjpegUrl : null);
        // A saved token file belongs to the saved URL, so it is not sent to a different explicit URL.
        var tokenFile = given.TokenFile ?? (mjpeg && given.Url is null ? input?.TokenFilePath : null);
        var model = given.Model ?? (saved is { Model: { Enabled: true, DefinitionPath: { } definition } } ? definition : null);
        var crop = saved?.Preprocessing is { UseCrop: true } used ? used : null;
        var preprocessing = saved?.Preprocessing;
        var expressions = saved?.Expressions ?? AppSettings.Default.Expressions;
        var osc = given.Osc ?? (!given.OscDryRun && model is not null && saved?.Osc is { Enabled: true } output ? output.Endpoint : null);
        return new(source, path, url, tokenFile, model,
            given.CropLeft ?? crop?.CropLeft ?? 0, given.CropTop ?? crop?.CropTop ?? 0,
            given.CropWidth ?? crop?.CropWidth ?? 100, given.CropHeight ?? crop?.CropHeight ?? 100,
            given.Rotation ?? preprocessing?.Rotation ?? 0, given.Mirror || preprocessing?.Mirror == true,
            given.Calibration ?? (expressions.AdaptiveCalibration ? CalibrationMode.Adaptive : CalibrationMode.Off),
            given.Smoothing ?? (expressions.Smoothing ? SmoothingMode.OneEuro : SmoothingMode.Off),
            given.Tongue || expressions.Tongue, given.Deadzone ?? expressions.Deadzone, osc, given.OscDryRun,
            source == InputKind.Camera ? input?.Camera : null);
    }
}
