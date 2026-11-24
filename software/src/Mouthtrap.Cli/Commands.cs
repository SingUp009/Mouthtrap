using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ConsoleAppFramework;
using FlashCap;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inputs;
using Mouthtrap.Outputs;
using Mouthtrap.Sessions;

namespace Mouthtrap.Cli;

public enum InputKind { Camera, Image, Video, Mjpeg }
public enum CalibrationMode { Off, Adaptive }
public enum SmoothingMode { Off, OneEuro }

public sealed partial class Commands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>List cameras and their indexed capture modes as JSON.</summary>
    public async Task<int> Cameras(CancellationToken cancellationToken)
    {
        var devices = await Discover(cancellationToken);
        if (devices is null) return cancellationToken.IsCancellationRequested ? 130 : 1;
        Write(Console.Out, new
        {
            Type = "cameras",
            Devices = devices.Select((device, index) => new
            {
                Index = index,
                device.Name,
                Backend = device.DeviceType.ToString(),
                Identity = device.Identity.ToString(),
                Modes = device.Characteristics.Select((mode, modeIndex) => new
                {
                    Index = modeIndex,
                    mode.Width,
                    mode.Height,
                    Format = mode.PixelFormat.ToString(),
                    Fps = (double)mode.FramesPerSecond,
                    mode.IsDiscrete,
                    Supported = mode.PixelFormat != PixelFormats.Unknown
                })
            })
        });
        return devices.Length == 0 ? 2 : 0;
    }

    /// <summary>Capture camera, image, video or HTTP MJPEG frames; optionally run a model. Output is JSON Lines.</summary>
    /// <param name="source">Camera, Image, Video or Mjpeg (default: the saved input, else Mjpeg).</param>
    /// <param name="path">Image or video file path.</param>
    /// <param name="url">HTTP(S) MJPEG stream URL.</param>
    /// <param name="tokenFile">Optional file containing the stream bearer token (not placed on the command line).</param>
    /// <param name="calibration">Adaptive learns JawOpen's range and each other output's resting level; Off sends model values.</param>
    /// <param name="camera">Camera index from cameras; required unless the saved camera is connected.</param>
    /// <param name="mode">Mode index from cameras; required unless the saved mode is available.</param>
    /// <param name="model">Model definition JSON; omit to check capture and preprocessing.</param>
    /// <param name="seconds">Session limit including initialization, 0 for unlimited (maximum 86400).</param>
    /// <param name="cropLeft">Crop left in percent.</param>
    /// <param name="cropTop">Crop top in percent.</param>
    /// <param name="cropWidth">Crop width in percent.</param>
    /// <param name="cropHeight">Crop height in percent.</param>
    /// <param name="rotation">Clockwise rotation: 0, 90, 180 or 270.</param>
    /// <param name="mirror">Mirror horizontally after rotation.</param>
    /// <param name="smoothing">OneEuro smooths every sent expression; Off sends calibrated values as they are.</param>
    /// <param name="deadzone">Send values at or below this (0 to 0.5) as 0 and stretch the rest, so the mouth closes at rest.</param>
    /// <param name="tongue">Also send the tongue keys, which react to an open mouth with the Babble model.</param>
    /// <param name="osc">Send Unified Expressions to VRChat at this IP:port, such as 127.0.0.1:9000. Requires --model.</param>
    /// <param name="oscDryRun">Print the OSC packets as JSON Lines instead of sending them. Requires --model.</param>
    /// <param name="settings">Settings JSON saved by the GUI; options that are not given are taken from it.</param>
    /// <param name="savedSettings">Take options that are not given from the GUI's settings (%APPDATA%\Mouthtrap\settings.json).</param>
    [Command("run")]
    public Task<int> Run(InputKind? source = null, string? path = null, int camera = -1, int mode = -1,
        string? model = null, double seconds = 10, double? cropLeft = null, double? cropTop = null, double? cropWidth = null,
        double? cropHeight = null, int? rotation = null, bool mirror = false, string? url = null, string? tokenFile = null,
        CalibrationMode? calibration = null, SmoothingMode? smoothing = null, bool tongue = false, float? deadzone = null,
        string? osc = null, bool oscDryRun = false, string? settings = null, bool savedSettings = false,
        CancellationToken cancellationToken = default)
    {
        AppSettings? saved = null;
        if (settings is not null && savedSettings)
        {
            Error("arguments", "Use either --settings or --saved-settings, not both.");
            return Task.FromResult(2);
        }
        if ((settings ?? (savedSettings ? SettingsStore.DefaultPath : null)) is { } file)
        {
            switch (SettingsStore.Load(file))
            {
                case Ok<AppSettings>(var loaded):
                    saved = loaded;
                    break;
                case Err<SettingsError>(var error):
                    Error("settings", SettingsErrorMessage(error));
                    return Task.FromResult(2);
            }
        }
        var options = RunOptions.Resolve(new()
        {
            Source = source,
            Path = path,
            Url = url,
            TokenFile = tokenFile,
            Model = model,
            CropLeft = cropLeft,
            CropTop = cropTop,
            CropWidth = cropWidth,
            CropHeight = cropHeight,
            Rotation = rotation,
            Mirror = mirror,
            Calibration = calibration,
            Smoothing = smoothing,
            Tongue = tongue,
            Deadzone = deadzone,
            Osc = osc,
            OscDryRun = oscDryRun
        }, saved);
        return Run(options, camera, mode, seconds, cancellationToken);
    }

    // Why a run that ended without an error processed no frame. limitSeconds is the --seconds limit when that limit
    // ended the run, which includes starting the input: a connection still being set up is ended by it too.
    public static string NoFramesMessage(double? limitSeconds) => limitSeconds is { } limit
        ? string.Create(CultureInfo.InvariantCulture,
            $"No frame was processed within the {limit:0.###} s limit; the input may still have been starting. Try a longer --seconds.")
        : "The input ended without a frame being processed.";

    public static string SettingsErrorMessage(SettingsError error) => error switch
    {
        SettingsFileMissing(var path) => $"No settings file at {path}.",
        SettingsFileUnreadable(var path, var message) => $"Cannot read {path}: {message}",
        SettingsFileCorrupt(var path, var message) => $"{path} is not valid settings JSON: {message}",
        SettingsVersionUnsupported(var path, var version) => $"{path} has unsupported version {version}.",
        SettingsValueInvalid(var field, var message) => $"{field}: {message}",
        SettingsWriteFailed(var path, var message) => $"Cannot write {path}: {message}"
    };

    private Task<int> Run(RunOptions options, int camera, int mode, double seconds, CancellationToken cancellationToken)
    {
        var (source, path, url, tokenFile, model) = (options.Source, options.Path, options.Url, options.TokenFile, options.Model);
        var (cropLeft, cropTop, cropWidth, cropHeight) = (options.CropLeft, options.CropTop, options.CropWidth, options.CropHeight);
        var (rotation, mirror, calibration, smoothing) = (options.Rotation, options.Mirror, options.Calibration, options.Smoothing);
        var (tongue, deadzone, osc, oscDryRun) = (options.Tongue, options.Deadzone, options.Osc, options.OscDryRun);
        return StaDispatcher.Run(async dispatcher =>
        {
            var parsedDeadzone = Deadzone.Create(deadzone);
            if (parsedDeadzone is Err<InvalidValue>(var invalidDeadzone)) return Usage(invalidDeadzone.Message);
            var expressionOptions = new ExpressionOptions
            {
                AdaptiveCalibration = calibration == CalibrationMode.Adaptive,
                Smoothing = smoothing == SmoothingMode.OneEuro,
                Tongue = tongue,
                Deadzone = parsedDeadzone.UnwrapOr(Deadzone.Default)
            };
            var parsedOsc = OscOptions.Parse(osc, oscDryRun);
            if (parsedOsc is Err<string>(var oscError)) return Usage(oscError);
            var oscTarget = parsedOsc.UnwrapOr(null);
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) return Usage("seconds must be between 0 and 86400.");
            var preprocessed = new PreprocessingSettings
            {
                UseCrop = true,
                CropLeft = cropLeft,
                CropTop = cropTop,
                CropWidth = cropWidth,
                CropHeight = cropHeight,
                Rotation = rotation,
                Mirror = mirror
            }.ToPreprocess();
            if (preprocessed is Err<InvalidValue>(var invalidPreprocessing)) return Usage(invalidPreprocessing.Message);
            var preprocessing = preprocessed.UnwrapOr(PreprocessSettings.Default);
            IFrameSource input;
            if (source == InputKind.Camera)
            {
                var devices = await Discover(cancellationToken);
                if (devices is null) return cancellationToken.IsCancellationRequested ? 130 : 1;
                if (camera < 0 && options.Camera is { } saved)
                {
                    camera = SavedCamera.IndexOf(devices, saved);
                    if (camera < 0) return Usage($"The saved camera '{saved.Name}' is not connected; select --camera from cameras.");
                    if (mode < 0 && saved.Mode is { } savedMode)
                        mode = Array.FindIndex(devices[camera].Characteristics, item => SavedCamera.Matches(item, savedMode));
                }
                if (camera < 0 || camera >= devices.Length) return Usage("Select --camera from cameras.");
                var descriptor = devices[camera];
                if (mode < 0 || mode >= descriptor.Characteristics.Length || descriptor.Characteristics[mode].PixelFormat == PixelFormats.Unknown)
                    return Usage("Select a supported --mode from cameras.");
                input = new CameraFrameSource(descriptor, descriptor.Characteristics[mode]);
            }
            else if (source == InputKind.Mjpeg)
            {
                if (StreamUrl.Create(url) is not Ok<StreamUrl>(var address))
                    return Usage("Mjpeg requires an absolute HTTP(S) --url without a user name or password.");
                // The token file is checked here so that a bad file is a usage error; the input reads it again when it starts.
                if (tokenFile is not null && StreamTokenFile.Read(tokenFile) is Err<InputSettingsInvalid>(var invalid))
                {
                    Error("token_file", invalid.Message);
                    return 2;
                }
                input = new MjpegStreamInput(address, tokenFile);
            }
            else if (string.IsNullOrWhiteSpace(path)) return Usage("Image and Video require --path.");
            else if (source == InputKind.Image) input = new ImageFileSource(path);
            else if (source == InputKind.Video) input = new VideoFileSource(path, dispatcher);
            else return Usage("Unsupported source.");

            var created = TrackingSession.Create(new(input)
            {
                Preprocessing = preprocessing,
                Model = model,
                Expressions = expressionOptions,
                Osc = oscTarget is null ? null : OscTransport(oscTarget)
            });
            if (created is Err<SessionSetupError>(var setupError)) return Usage(SetupErrorMessage(setupError));
            if (created is not Ok<TrackingSession>(var tracking)) throw new UnreachableException();

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            var clock = TimeProvider.System;
            var startedAt = clock.GetTimestamp();
            FrameReport? last = null;
            var imageCompleted = false;
            var sink = new ReportSink(report =>
            {
                last = report;
                Write(Console.Out, new { Type = "frame", Frame = report });
                if (source == InputKind.Image) { imageCompleted = true; stop.Cancel(); }
            });
            var exit = await tracking.Run(sink).RunExitAsync(new(clock), stop.Token);
            var code = exit switch
            {
                Ok<Unit> => 0,
                Interrupted => cancellationToken.IsCancellationRequested ? 130 : 0,
                Err<TrackingError>(var error) => ExpectedError(error),
                Die die => Defect(die)
            };
            if (code == 0 && last is null)
            {
                // Only --seconds interrupts a run that the user did not cancel.
                Error("no_frames", NoFramesMessage(exit is Interrupted ? seconds : null));
                code = 1;
            }
            var elapsed = clock.GetElapsedTime(startedAt).TotalSeconds;
            var statistics = tracking.Frames.Statistics;
            Write(Console.Out, new
            {
                Type = "summary",
                ExitCode = code,
                Reason = code == 130 ? "cancelled" : code != 0 ? "failed" : imageCompleted ? "image" : stop.IsCancellationRequested ? "duration" : "eof",
                Seconds = elapsed,
                statistics.Received,
                statistics.Replaced,
                Processed = last?.Processed ?? 0,
                CaptureFps = source == InputKind.Image ? (double?)null : statistics.Received / Math.Max(elapsed, double.Epsilon),
                OscSent = tracking.UsesOsc ? tracking.OscSent : (long?)null
            });
            return code;
        });
    }

    private static async Task<CaptureDeviceDescriptor[]?> Discover(CancellationToken cancellationToken)
    {
        var exit = await CameraDiagnostics.Enumerate<CameraEnvironment>()
            .RunExitAsync(new CameraEnvironment(new CaptureDevices()), cancellationToken);
        return exit switch
        {
            Ok<CaptureDeviceDescriptor[]>(var devices) => devices,
            Err<CameraDiagnosticError>(var error) => EnumerationError(error),
            Die die => EnumerationDefect(die),
            Interrupted => null
        };
    }

    private static CaptureDeviceDescriptor[]? EnumerationError(CameraDiagnosticError error)
    {
        _ = error switch
        {
            UnsupportedPlatform => Failure("unsupported_platform", "Windows is required."),
            CameraEnumerationFailed(var code, var message) => Failure("camera_enumeration", message, code)
        };
        return null;
    }
    private static CaptureDeviceDescriptor[]? EnumerationDefect(Die die) { Defect(die); return null; }
    private static int ExpectedError(TrackingError error) => error switch
    {
        TrackingInputFailed(var input) => input switch
        {
            InputSettingsInvalid(var message) => Failure("input_settings", message),
            InputUnavailable(var message, var code) => Failure("input_unavailable", message, code),
            InputLost(var message) => Failure("input_lost", message),
            InputDataInvalid(var message) => Failure("input_data", message)
        },
        TrackingModelFailed(var model) => model switch
        {
            ModelFileMissing(var path) => Failure("model_file_missing", $"No model file at {path}."),
            ModelLoadFailed(var message) => Failure("model_load", message),
            ModelContractMismatch(var message) => Failure("model_contract", message),
            ModelInferenceFailed(var message) => Failure("model_inference", message)
        },
        TrackingOutputFailed(var output) => Failure("output", output.Message, output.Code)
    };
    private static string SetupErrorMessage(SessionSetupError error) => error switch
    {
        OscRequiresModel => "OSC output requires --model."
    };
    private static int Usage(string message) { Error("arguments", message); return 2; }
    private static int Failure(string kind, string message, int? hResult = null) { Error(kind, message, hResult); return 1; }
    private static int Defect(Die die)
    {
        Write(Console.Error, new
        {
            Type = "error",
            Kind = "defect",
            Message = die.Exception.ToString(),
            Suppressed = die.Suppressed.Select(item => item.Case.Value?.ToString()).ToArray()
        });
        return 1;
    }
    private static void Error(string kind, string? message, int? hResult = null) =>
        Write(Console.Error, new { Type = "error", Kind = kind, Message = message, HResult = hResult });
    private static void Write(TextWriter writer, object value) => writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
