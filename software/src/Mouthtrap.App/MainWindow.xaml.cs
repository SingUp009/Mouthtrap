using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashCap;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inputs;
using Mouthtrap.Outputs;
using Mouthtrap.Sessions;
using Microsoft.Win32;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace Mouthtrap.App;

public partial class MainWindow : Window
{
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer renderTimer;
    private Task? enumerationTask;
    private Task? sessionTask;
    private CancellationTokenSource? sessionCancellation;
    // A preview that failed to render stops the session, which then reports this defect rather than a stop.
    private Exception? displayFailure;
    private TrackingSession? session;
    private LatestFrameSink? display;
    private bool refreshing;
    private bool closing;
    private bool allowClose;
    private long displayed;
    private long renderedProcessed;
    private long fpsAt;
    private long fpsCount;
    private long processingCount;
    private double processingFps;
    private readonly string?[] inputFiles = new string?[4];
    private PreprocessSettings preprocessing = PreprocessSettings.Default;
    private WriteableBitmap? sourceBitmap;
    private WriteableBitmap? processedBitmap;
    private string FpsLabel => InputKind.SelectedIndex switch { 0 => Strings.Get("FpsCamera"), 3 => Strings.Get("FpsStream"), _ => Strings.Get("FpsVideo") };
    private string? modelDefinitionPath;
    private string? tokenFilePath;
    private Point? dragStart;

    // Settings are saved on each change once they have been applied to the controls; restoring a selection
    // (such as the saved camera after enumeration) does not count as a change.
    private readonly string settingsPath;
    private AppSettings settings = AppSettings.Default;
    private PreprocessingSettings savedPreprocessing = AppSettings.Default.Preprocessing;
    private int restoring = 1;
    // The language the window was built in; a different choice applies after a restart.
    private readonly AppLanguage shownLanguage;

    public MainWindow() : this(SettingsStore.DefaultPath) { }

    public MainWindow(string settingsPath)
    {
        this.settingsPath = settingsPath;
        InitializeComponent();
        renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, RenderFrame, Dispatcher);
        renderTimer.Stop();
        LoadSettings();
        shownLanguage = settings.Language;
        restoring = 0;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (refreshing || sessionCancellation is not null || closing) return;
        refreshing = true;
        UpdateControls();
        if (InputKind.SelectedIndex == 0) StatusText.Text = Strings.Get("DetectingCameras");
        try
        {
            var run = CameraDiagnostics.Enumerate<CameraEnvironment>().OnThreadPool()
                .RunExitAsync(new(new CaptureDevices()), lifetime.Token).AsTask();
            enumerationTask = run;
            var exit = await run;
            if (closing) return;
            switch (exit)
            {
                case Ok<CaptureDeviceDescriptor[]>(var devices):
                    restoring++;
                    try
                    {
                        Cameras.ItemsSource = devices;
                        var saved = settings.Input.Camera;
                        Cameras.SelectedItem = saved is not null && SavedCamera.IndexOf(devices, saved) is >= 0 and var index ? devices[index] : null;
                    }
                    finally { restoring--; }
                    ShowCameraResult(devices.Length == 0 ? Strings.Get("NoCameras") : IdleGuidance);
                    break;
                case Err<CameraDiagnosticError>(CameraEnumerationFailed(var code, var message)):
                    ShowCameraResult(Strings.Format("DetectFailed", code, message));
                    break;
                case Err<CameraDiagnosticError>(UnsupportedPlatform):
                    ShowCameraResult(Strings.Get("NeedsWindows"));
                    break;
                case Die die:
                    ReportDefect(die);
                    break;
                case Interrupted:
                    ShowCameraResult(Strings.Get("DetectCancelled"));
                    break;
            }
        }
        finally
        {
            refreshing = false;
            UpdateControls();
        }
    }

    // What to check before starting, for the selected input.
    private string IdleGuidance => InputKind.SelectedIndex switch
    {
        0 => Strings.Get("GuidanceCamera"),
        3 => Strings.Get("GuidanceStream"),
        _ => Strings.Get("GuidanceFile")
    };

    // Camera detection runs at startup whatever the input is; its result is only news when the camera is the input.
    private void ShowCameraResult(string text)
    {
        if (sessionCancellation is null) StatusText.Text = InputKind.SelectedIndex == 0 ? text : IdleGuidance;
    }

    private void Camera_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Cameras.SelectedItem is CaptureDeviceDescriptor descriptor)
        {
            var modes = descriptor.Characteristics.Where(mode => mode.PixelFormat != FlashCap.PixelFormats.Unknown).ToArray();
            restoring++;
            try
            {
                Modes.ItemsSource = modes;
                var saved = settings.Input.Camera is { } camera && camera.Identity == descriptor.Identity.ToString() ? camera.Mode : null;
                Modes.SelectedItem = modes.FirstOrDefault(mode => saved is not null && SavedCamera.Matches(mode, saved))
                    ?? modes.FirstOrDefault(mode => mode.Width == 1280 && mode.Height == 720 &&
                        mode.PixelFormat == FlashCap.PixelFormats.JPEG && Math.Abs((double)mode.FramesPerSecond - 30) <= 0.05);
            }
            finally { restoring--; }
        }
        else Modes.ItemsSource = null;
        SaveSettings();
        UpdateControls();
    }

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSettings();
        UpdateControls();
    }

    private void InputKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilePathText is null) return;
        FilePathText.Text = inputFiles[InputKind.SelectedIndex] ?? Strings.Get("NoFile");
        if (sessionCancellation is null) StatusText.Text = IdleGuidance;
        SaveSettings();
        UpdateControls();
    }

    private void TokenFile_Click(object sender, RoutedEventArgs e)
    {
        // Only the path is kept; the token is read when a session starts.
        var dialog = new OpenFileDialog { Title = Strings.Get("TokenDialogTitle"), Filter = Strings.Get("TokenDialogFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        tokenFilePath = dialog.FileName;
        TokenFileText.Text = dialog.FileName;
        SaveSettings();
        UpdateControls();
    }

    private void MjpegUrl_TextChanged(object sender, TextChangedEventArgs e) => UpdateControls();

    private StreamUrl? MjpegAddress => StreamUrl.Create(MjpegUrl.Text.Trim()) is Ok<StreamUrl>(var address) ? address : null;

    private void File_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = InputKind.SelectedIndex == 1 ? Strings.Get("ImageDialogTitle") : Strings.Get("VideoDialogTitle"),
            Filter = InputKind.SelectedIndex == 1 ? Strings.Get("ImageDialogFilter") : Strings.Get("VideoDialogFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        inputFiles[InputKind.SelectedIndex] = dialog.FileName;
        FilePathText.Text = dialog.FileName;
        SaveSettings();
        UpdateControls();
    }

    private IFrameSource? SelectedSource() => InputKind.SelectedIndex switch
    {
        0 when Cameras.SelectedItem is CaptureDeviceDescriptor descriptor && Modes.SelectedItem is VideoCharacteristics mode => new CameraFrameSource(descriptor, mode),
        1 when inputFiles[1] is { } path => new ImageFileSource(path),
        2 when inputFiles[2] is { } path => new VideoFileSource(path, Dispatcher),
        3 when MjpegAddress is { } address => new MjpegStreamInput(address, tokenFilePath),
        _ => null
    };

    private void EnableTracking_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelFileButton is null) return;
        SaveSettings();
        UpdateControls();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        // Restoring, including the XAML defaults raised while the window is built.
        if (restoring > 0) return;
        SaveSettings();
        UpdateControls();
    }

    private void Setting_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveSettings();

    // The window reads its text once, so a new language is saved now and shown after a restart.
    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageHint is null || restoring > 0) return;
        SaveSettings();
        LanguageHint.Visibility = (AppLanguage)LanguageChoice.SelectedIndex == shownLanguage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ModelFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Strings.Get("ModelDialogTitle"), Filter = Strings.Get("ModelDialogFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        modelDefinitionPath = dialog.FileName;
        ModelPathText.Text = dialog.FileName;
        SaveSettings();
        UpdateControls();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sessionCancellation is not null || refreshing || closing || SelectedSource() is not { } source) return;
        if (ReadExpressionOptions() is not { } options)
        {
            StatusText.Text = Strings.Get("DeadzoneInvalid");
            return;
        }
        Core.OscEndpoint? endpoint = null;
        if (EnableTracking.IsChecked == true && OscEnabled.IsChecked == true)
        {
            if (Core.OscEndpoint.Create(OscEndpoint.Text.Trim()) is not Ok<Core.OscEndpoint>(var parsed))
            {
                StatusText.Text = Strings.Get("OscEndpointInvalid");
                return;
            }
            endpoint = parsed;
        }
        var created = TrackingSession.Create(new(source)
        {
            Preprocessing = preprocessing,
            Model = EnableTracking.IsChecked == true ? modelDefinitionPath : null,
            Expressions = options,
            Osc = endpoint is null ? null : VrchatOscSession.Udp(endpoint)
        });
        // OSC is offered only while tracking with a model is on, so a setup error is a bug here.
        if (created is not Ok<TrackingSession>(var tracking)) throw new InvalidOperationException("OSC was enabled without a model.");
        SaveSettings();

        sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        displayFailure = null;
        session = tracking;
        display = new LatestFrameSink();
        OscStatusText.Text = tracking.UsesOsc ? Strings.Format("OscPreparing", endpoint) : Strings.Get("OscOff");
        displayed = renderedProcessed = fpsCount = processingCount = 0;
        processingFps = 0;
        fpsAt = clock.GetTimestamp();
        ClearPreview();
        CaptureFpsText.Text = InputKind.SelectedIndex == 1 ? Strings.Get("StillLoading") : Strings.Format("FpsMeasuring", FpsLabel);
        PreprocessStatsText.Text = Strings.Get("PreprocessWaiting");
        FrameStatsText.Text = Strings.Get("FrameStatsStart");
        StatusText.Text = Strings.Get("StartingInput");
        InvalidateTracking();
        TrackingStatsText.Text = tracking.UsesModel ? Strings.Get("ModelLoading") : Strings.Get("TrackingOff");
        UpdateControls();
        renderTimer.Start();
        sessionTask = RunSessionAsync(tracking, display, sessionCancellation.Token);
        await sessionTask;
    }

    private ExpressionOptions? ReadExpressionOptions()
    {
        if (!float.TryParse(Deadzone.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            Core.Deadzone.Create(value) is not Ok<Core.Deadzone>(var deadzone))
            return null;
        return new ExpressionOptions
        {
            AdaptiveCalibration = AdaptiveCalibration.IsChecked == true,
            Smoothing = Smoothing.IsChecked == true,
            Tongue = Tongue.IsChecked == true,
            Deadzone = deadzone
        };
    }

    // With an OSC endpoint the session owns a UDP socket for its whole run and sends zeros to VRChat when it ends.
    private async Task RunSessionAsync(TrackingSession tracking, ITrackingSink sink, CancellationToken cancellationToken)
    {
        try
        {
            var exit = await tracking.Run(sink).RunExitAsync(new(clock), cancellationToken);
            StatusText.Text = exit switch
            {
                Ok<Unit> => Strings.Get("Completed"),
                Err<TrackingError>(TrackingInputFailed(var error)) => InputErrorText(error),
                Err<TrackingError>(TrackingModelFailed(var error)) => ModelErrorText(error),
                Err<TrackingError>(TrackingOutputFailed(var error)) => Strings.Format("OscFailed", error.Message),
                Die die => ReportDefect(die),
                Interrupted when displayFailure is { } failure => ReportDefect("DisplayFailure", failure, []),
                Interrupted => Strings.Get("Stopped"),
            };
        }
        finally
        {
            renderTimer.Stop();
            CaptureFpsText.Text = Strings.Get("InputFpsStopped");
            OscStatusText.Text = tracking.UsesOsc ? Strings.Format("OscStopped", tracking.OscSent) : Strings.Get("OscOff");
            session = null;
            display = null;
            ClearPreview();
            InvalidateTracking();
            TrackingStatsText.Text = Strings.Get("TrackingStopped");
            sessionCancellation?.Dispose();
            sessionCancellation = null;
            UpdateControls();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        if (sessionCancellation is null) return;
        StatusText.Text = Strings.Get("Stopping");
        sessionCancellation.Cancel();
        UpdateControls();
        if (sessionTask is not null) await sessionTask;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (session is not { } tracking || display is not { } sink) return;
        try
        {
            var frame = sink.Latest;
            if (frame is not null && frame.Diagnostics.Processed != renderedProcessed)
            {
                renderedProcessed = frame.Diagnostics.Processed;
                ShowFrame(frame);
            }
            var elapsed = clock.GetElapsedTime(fpsAt);
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                var count = tracking.Frames.Statistics.Received;
                CaptureFpsText.Text = InputKind.SelectedIndex == 1 ? Strings.Get("StillHolding")
                    : string.Create(CultureInfo.CurrentCulture, $"{FpsLabel}: {(count - fpsCount) / elapsed.TotalSeconds:F1}");
                var processed = frame?.Diagnostics.Processed ?? 0;
                processingFps = (processed - processingCount) / elapsed.TotalSeconds;
                fpsCount = count;
                processingCount = processed;
                fpsAt = clock.GetTimestamp();
                if (tracking.UsesOsc) OscStatusText.Text = Strings.Format("OscSending", tracking.OscSent);
            }
        }
        catch (Exception exception)
        {
            displayFailure ??= exception;
            renderTimer.Stop();
            StatusText.Text = Strings.Get("DisplayFailed");
            sessionCancellation?.Cancel();
        }
    }

    private void ShowFrame(TrackingFrame frame)
    {
        var diagnostics = frame.Diagnostics;
        var source = diagnostics.Source;
        sourceBitmap = Draw(sourceBitmap, source.Width, source.Height, source.Data, source.Stride);
        Preview.Source = sourceBitmap;
        processedBitmap = Draw(processedBitmap, diagnostics.PixelWidth, diagnostics.PixelHeight, diagnostics.Pixels, diagnostics.PixelWidth * 3);
        ProcessedPreview.Source = processedBitmap;
        ProcessedTitleText.Text = Strings.Format("ProcessedTitle", diagnostics.PixelWidth, diagnostics.PixelHeight);
        PreviewHint.Visibility = Visibility.Collapsed;

        var crop = diagnostics.Settings.Preprocessing.Crop;
        var region = crop.ToPixels(source.Width, source.Height);
        PreviewSurface.Width = source.Width;
        PreviewSurface.Height = source.Height;
        if (dragStart is null)
        {
            Canvas.SetLeft(CropOutline, region.X);
            Canvas.SetTop(CropOutline, region.Y);
            CropOutline.Width = region.Width;
            CropOutline.Height = region.Height;
            CropOutline.Visibility = crop == CropRegion.FullFrame ? Visibility.Collapsed : Visibility.Visible;
        }
        PreprocessStatsText.Text = Strings.Format("PreprocessStats", source.Width, source.Height, region.X, region.Y, region.Width, region.Height,
            diagnostics.PreprocessMilliseconds);

        displayed++;
        var statistics = diagnostics.Statistics;
        FrameStatsText.Text = Strings.Format("FrameStats", statistics.Received, diagnostics.Processed, displayed, statistics.Replaced, diagnostics.FrameAgeMilliseconds);
        if (sessionCancellation?.IsCancellationRequested == false)
            StatusText.Text = Strings.Get("Previewing");
        // Results computed with settings older than the last change are not shown.
        if (session is { UsesModel: true } tracking && diagnostics.Settings.Revision == tracking.Control.Current.Revision) RenderTracking(frame);
    }

    private static WriteableBitmap Draw(WriteableBitmap? bitmap, int width, int height, byte[] rgb24, int stride)
    {
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Rgb24, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), rgb24, stride, 0);
        return bitmap;
    }

    private void ClearPreview()
    {
        Preview.Source = null;
        ProcessedPreview.Source = null;
        sourceBitmap = processedBitmap = null;
        ProcessedTitleText.Text = Strings.Get("ProcessedIdle");
        CropOutline.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Visible;
    }

    private void UpdateControls()
    {
        if (StartButton is null) return;
        var idle = !closing && !refreshing && sessionCancellation is null;
        var (state, brush) = closing ? ("StateClosing", "StoppingBrush")
            : sessionCancellation is { IsCancellationRequested: true } ? ("StateStopping", "StoppingBrush")
            : sessionCancellation is not null ? ("StateRunning", "RunningBrush")
            : refreshing ? ("StateDetecting", "StoppingBrush")
            : ("StateIdle", "IdleBrush");
        SessionStateText.Text = Strings.Get(state);
        SessionDot.Fill = (System.Windows.Media.Brush)FindResource(brush);
        InputKind.IsEnabled = idle;
        var camera = InputKind.SelectedIndex == 0;
        var file = InputKind.SelectedIndex is 1 or 2;
        var mjpeg = InputKind.SelectedIndex == 3;
        CameraOptions.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        MjpegOptions.Visibility = mjpeg ? Visibility.Visible : Visibility.Collapsed;
        FileButton.Visibility = FilePathText.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        CameraOptions.IsEnabled = RefreshButton.IsEnabled = idle && camera;
        FileButton.IsEnabled = idle && file;
        MjpegOptions.IsEnabled = idle && mjpeg;
        EnableTracking.IsEnabled = ModelFileButton.IsEnabled = idle;
        var modeled = idle && EnableTracking.IsChecked == true;
        AdaptiveCalibration.IsEnabled = Smoothing.IsEnabled = Deadzone.IsEnabled = Tongue.IsEnabled = modeled;
        OscEnabled.IsEnabled = modeled;
        OscEndpoint.IsEnabled = modeled && OscEnabled.IsChecked == true;
        ResetCalibrationButton.IsEnabled = !closing && sessionCancellation is { IsCancellationRequested: false }
            && session is { UsesModel: true } && AdaptiveCalibration.IsChecked == true;
        StartButton.IsEnabled = idle && SelectedSource() is not null && (EnableTracking.IsChecked != true || modelDefinitionPath is not null);
        StopButton.IsEnabled = !closing && sessionCancellation is { IsCancellationRequested: false };
    }

    private static string InputErrorText(FrameSourceError error) => error switch
    {
        InputSettingsInvalid(var message) => Strings.Format("InputSettingsError", message),
        InputUnavailable(var message, int code) => Strings.Format("InputUnavailableWithCode", message, code),
        InputUnavailable(var message, _) => Strings.Format("InputUnavailable", message),
        InputLost(var message) => Strings.Format("InputLost", message),
        InputDataInvalid(var message) => Strings.Format("InputDataError", message)
    };

    private static string ModelErrorText(ModelError error) => error switch
    {
        ModelFileMissing(var path) => Strings.Format("ModelMissing", path),
        ModelLoadFailed(var message) => Strings.Format("ModelLoadFailed", message),
        ModelContractMismatch(var message) => Strings.Format("ModelContract", message),
        ModelInferenceFailed(var message) => Strings.Format("InferenceFailed", message)
    };

    private void ResetCalibration_Click(object sender, RoutedEventArgs e)
    {
        session?.Control.ResetCalibration();
        InvalidateTracking();
    }

    private void InvalidateTracking()
    {
        JawOpenText.Text = Strings.Get("JawOpenNone");
        JawOpenBar.Visibility = Visibility.Collapsed;
        AdditionalExpressionPanel.Visibility = Visibility.Collapsed;
        AdditionalExpressionText.Text = "";
    }

    private void RenderTracking(TrackingFrame frame)
    {
        if (frame.Result is not { } result) return;
        var (estimate, calibration, diagnostics) = (result.Estimate, result.Calibration, frame.Diagnostics);
        var value = calibration?.JawOpen ?? estimate.JawOpen;
        var label = Strings.Get(calibration is { IsReady: true } ? "CalibrationReady" : calibration is not null ? "CalibrationLearning" : "Uncalibrated");
        JawOpenText.Text = Strings.Format("JawOpenValue", value, label);
        JawOpenBar.Value = value;
        JawOpenBar.Visibility = Visibility.Visible;
        var additional = estimate.AdditionalExpressions;
        AdditionalExpressionPanel.Visibility = additional.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        AdditionalExpressionText.Text = string.Join("\n", additional.Select(item => $"{item.Shape}: {item.Value:F3}  (raw: {item.Raw:F4})"));
        TrackingStatsText.Text = Strings.Format("TrackingStats", diagnostics.PixelWidth, diagnostics.PixelHeight, estimate.RawJawOpen,
                diagnostics.InferenceMilliseconds, processingFps, diagnostics.FrameAgeMilliseconds, diagnostics.Processed) +
            (calibration is { IsReady: true } learned ? Strings.Format("LearnedRange", learned.Lower, learned.Upper) : "") +
            Strings.Get("NoFaceGuarantee");
    }

    private void ApplyPreprocessing_Click(object sender, RoutedEventArgs e)
    {
        var useCrop = CropEnabled.IsChecked == true;
        var typed = (Left: Percent(CropX.Text), Top: Percent(CropY.Text), Width: Percent(CropWidth.Text), Height: Percent(CropHeight.Text));
        var crop = !useCrop ? new Ok<CropRegion>(CropRegion.FullFrame)
            : typed is ({ } left, { } top, { } width, { } height) ? CropRegion.FromPercent(left, top, width, height)
            : new Err<InvalidValue>(new("crop", "The crop must be numbers."));
        if (crop.Map(region => new PreprocessSettings(region, (QuarterTurn)Rotation.SelectedIndex, Mirror.IsChecked == true))
            is not Ok<PreprocessSettings>(var next))
        {
            PreprocessStatusText.Text = Strings.Get("CropInvalid");
            return;
        }
        preprocessing = next;
        savedPreprocessing = useCrop
            ? new()
            {
                UseCrop = true,
                CropLeft = typed.Left!.Value,
                CropTop = typed.Top!.Value,
                CropWidth = typed.Width!.Value,
                CropHeight = typed.Height!.Value
            }
            : savedPreprocessing with { UseCrop = false };
        savedPreprocessing = savedPreprocessing with { Rotation = PreprocessingSettings.TurnToRotation(next.Rotation), Mirror = next.Mirror };
        session?.Control.Update(next);
        InvalidateTracking();
        PreprocessStatusText.Text = CropEnabled.IsChecked == true ? Strings.Get("CropApplied") : Strings.Get("CropFull");
        SaveSettings();
    }

    private static double? Percent(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : null;

    // Dragging on the input preview selects the crop in source pixels and applies it.
    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Preview.Source is null || closing) return;
        var start = Clamp(e.GetPosition(PreviewSurface));
        dragStart = start;
        PreviewSurface.CaptureMouse();
        UpdateDrag(start, start);
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragStart is { } start) UpdateDrag(start, Clamp(e.GetPosition(PreviewSurface)));
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is not { } start) return;
        var end = Clamp(e.GetPosition(PreviewSurface));
        dragStart = null;
        PreviewSurface.ReleaseMouseCapture();
        // Whole percents keep the fields readable; a click without a drag keeps the previous crop.
        var x = Math.Floor(Math.Min(start.X, end.X) / PreviewSurface.Width * 100);
        var y = Math.Floor(Math.Min(start.Y, end.Y) / PreviewSurface.Height * 100);
        var width = Math.Min(100, Math.Ceiling(Math.Max(start.X, end.X) / PreviewSurface.Width * 100)) - x;
        var height = Math.Min(100, Math.Ceiling(Math.Max(start.Y, end.Y) / PreviewSurface.Height * 100)) - y;
        if (Math.Abs(end.X - start.X) < 4 || Math.Abs(end.Y - start.Y) < 4 || width < 1 || height < 1)
        {
            PreprocessStatusText.Text = Strings.Get("CropDragTooSmall");
            return;
        }
        CropX.Text = x.ToString(CultureInfo.CurrentCulture);
        CropY.Text = y.ToString(CultureInfo.CurrentCulture);
        CropWidth.Text = width.ToString(CultureInfo.CurrentCulture);
        CropHeight.Text = height.ToString(CultureInfo.CurrentCulture);
        CropEnabled.IsChecked = true;
        ApplyPreprocessing_Click(sender, e);
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e) => dragStart = null;

    private Point Clamp(Point point) =>
        new(Math.Clamp(point.X, 0, PreviewSurface.Width), Math.Clamp(point.Y, 0, PreviewSurface.Height));

    private void UpdateDrag(Point start, Point point)
    {
        Canvas.SetLeft(CropOutline, Math.Min(start.X, point.X));
        Canvas.SetTop(CropOutline, Math.Min(start.Y, point.Y));
        CropOutline.Width = Math.Abs(point.X - start.X);
        CropOutline.Height = Math.Abs(point.Y - start.Y);
        CropOutline.Visibility = Visibility.Visible;
    }

    private void LoadSettings()
    {
        switch (SettingsStore.Load(settingsPath))
        {
            case Ok<AppSettings>(var loaded):
                settings = loaded;
                ShowSettingsStatus(Strings.Format("SettingsRestored", settingsPath), problem: false);
                break;
            case Err<SettingsError>(var error):
                ShowSettingsStatus(SettingsErrorText(error, loading: true), problem: error is not SettingsFileMissing);
                break;
        }
        var input = settings.Input;
        InputKind.SelectedIndex = (int)input.Kind;
        inputFiles[1] = input.ImagePath;
        inputFiles[2] = input.VideoPath;
        FilePathText.Text = inputFiles[InputKind.SelectedIndex] ?? Strings.Get("NoFile");
        MjpegUrl.Text = input.MjpegUrl ?? "";
        tokenFilePath = input.TokenFilePath;
        TokenFileText.Text = tokenFilePath ?? Strings.Get("NoToken");

        modelDefinitionPath = settings.Model.DefinitionPath;
        if (modelDefinitionPath is not null) ModelPathText.Text = modelDefinitionPath;
        EnableTracking.IsChecked = settings.Model.Enabled;

        savedPreprocessing = settings.Preprocessing;
        CropEnabled.IsChecked = savedPreprocessing.UseCrop;
        CropX.Text = savedPreprocessing.CropLeft.ToString(CultureInfo.CurrentCulture);
        CropY.Text = savedPreprocessing.CropTop.ToString(CultureInfo.CurrentCulture);
        CropWidth.Text = savedPreprocessing.CropWidth.ToString(CultureInfo.CurrentCulture);
        CropHeight.Text = savedPreprocessing.CropHeight.ToString(CultureInfo.CurrentCulture);
        Rotation.SelectedIndex = savedPreprocessing.Rotation / 90;
        Mirror.IsChecked = savedPreprocessing.Mirror;
        // The store validated the saved crop and rotation.
        preprocessing = savedPreprocessing.ToPreprocess().UnwrapOr(PreprocessSettings.Default);
        if (savedPreprocessing != AppSettings.Default.Preprocessing)
            PreprocessStatusText.Text = savedPreprocessing.UseCrop ? Strings.Get("SavedCropApplied") : Strings.Get("SavedFull");

        var expressions = settings.Expressions;
        AdaptiveCalibration.IsChecked = expressions.AdaptiveCalibration;
        Smoothing.IsChecked = expressions.Smoothing;
        Deadzone.Text = expressions.Deadzone.ToString(CultureInfo.CurrentCulture);
        Tongue.IsChecked = expressions.Tongue;
        OscEnabled.IsChecked = settings.Osc.Enabled;
        OscEndpoint.Text = settings.Osc.Endpoint;
        LanguageChoice.SelectedIndex = (int)settings.Language;
        StatusText.Text = IdleGuidance;
        UpdateControls();
    }

    // The settings shown by the controls. A text field that does not parse keeps its last saved value, and a camera
    // that is not connected now stays as saved.
    private AppSettings CurrentSettings()
    {
        var camera = Cameras.SelectedItem is CaptureDeviceDescriptor descriptor
            ? new CameraSettings(descriptor.Name, descriptor.Identity.ToString()!, Modes.SelectedItem is VideoCharacteristics mode
                ? new(mode.Width, mode.Height, mode.PixelFormat.ToString(), (double)mode.FramesPerSecond) : null)
            : settings.Input.Camera;
        var url = MjpegUrl.Text.Trim();
        var check = AdaptiveCalibration.IsChecked == true;
        var expressions = ReadExpressionOptions() is { } options
            ? new ExpressionSettings { AdaptiveCalibration = options.AdaptiveCalibration, Smoothing = options.Smoothing, Deadzone = options.Deadzone.Value, Tongue = options.Tongue }
            : settings.Expressions with { AdaptiveCalibration = check, Smoothing = Smoothing.IsChecked == true, Tongue = Tongue.IsChecked == true };
        var endpoint = OscEndpoint.Text.Trim();
        return settings with
        {
            Input = new()
            {
                Kind = (InputSourceKind)InputKind.SelectedIndex,
                Camera = camera,
                ImagePath = inputFiles[1],
                VideoPath = inputFiles[2],
                MjpegUrl = url.Length == 0 ? null : MjpegAddress is not null ? url : settings.Input.MjpegUrl,
                TokenFilePath = tokenFilePath
            },
            Model = new() { Enabled = EnableTracking.IsChecked == true, DefinitionPath = modelDefinitionPath },
            Preprocessing = savedPreprocessing,
            Expressions = expressions,
            Osc = new() { Enabled = OscEnabled.IsChecked == true, Endpoint = Core.OscEndpoint.Create(endpoint) is Ok<Core.OscEndpoint> ? endpoint : settings.Osc.Endpoint },
            Language = (AppLanguage)LanguageChoice.SelectedIndex
        };
    }

    private void SaveSettings()
    {
        if (restoring > 0 || closing) return;
        var next = CurrentSettings();
        if (next == settings) return;
        switch (SettingsStore.Save(settingsPath, next))
        {
            case Ok<Unit>:
                settings = next;
                ShowSettingsStatus(Strings.Format("SettingsSaved", settingsPath), problem: false);
                break;
            case Err<SettingsError>(var error):
                ShowSettingsStatus(SettingsErrorText(error, loading: false), problem: true);
                break;
        }
    }

    // The settings line is shown only when something needs the user's attention; the text is always kept current.
    private void ShowSettingsStatus(string text, bool problem)
    {
        SettingsStatusText.Text = text;
        SettingsStatusText.Visibility = problem ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SettingsErrorText(SettingsError error, bool loading) => error switch
    {
        SettingsFileMissing => Strings.Get("SettingsMissing"),
        SettingsFileUnreadable(var path, var message) => Strings.Format("SettingsUnreadable", path, message) + (loading ? Strings.Get("SettingsStartingWithDefaults") : ""),
        SettingsFileCorrupt(var path, var message) => Strings.Format("SettingsCorrupt", path, message),
        SettingsVersionUnsupported(var path, var version) => Strings.Format("SettingsVersion", path, version),
        SettingsValueInvalid(var field, var message) => Strings.Format("SettingsValueInvalid", field, message) +
            Strings.Get(loading ? "SettingsStartingWithDefaults" : "SettingsNotSaved"),
        SettingsWriteFailed(var path, var message) => Strings.Format("SettingsWriteFailed", path, message)
    };

    private string ReportDefect(Die die) =>
        ReportDefect("InputFailure", die.Exception, [.. die.Suppressed.Select(item => item.Case.Value?.ToString())]);

    private string ReportDefect(string name, Exception exception, string?[] suppressed)
    {
        Trace.TraceError(JsonSerializer.Serialize(new
        {
            Event = name,
            Exception = exception.ToString(),
            Suppressed = suppressed
        }));
        return StatusText.Text = Strings.Format("Defect", exception.Message);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        // A text field still being edited has not lost focus yet.
        SaveSettings();
        closing = true;
        UpdateControls();
        lifetime.Cancel();
        await StopAsync();
        if (enumerationTask is not null) await enumerationTask;
        lifetime.Dispose();
        // Completed cleanup can resume inline while WPF is still raising Closing.
        await Dispatcher.InvokeAsync(() => { allowClose = true; Close(); }, DispatcherPriority.Background);
    }

    // The tracking thread replaces the frame; the render timer reads the newest one.
    private sealed class LatestFrameSink : ITrackingSink
    {
        private TrackingFrame? latest;
        public TrackingFrame? Latest => Volatile.Read(ref latest);
        public Result<Unit, OutputFailed> Publish(TrackingFrame frame)
        {
            Volatile.Write(ref latest, frame);
            return new Ok<Unit>(Unit.Value);
        }
    }
}
