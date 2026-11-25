using System.IO;
using System.Windows;
using System.Windows.Controls;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Firmware;

namespace Mouthtrap.App;

// The Firmware card: downloads the latest published release, writes it to a XIAO on a serial port and, for the Wi-Fi
// firmware, stores the network and the stream token on the board and sets up the Wi-Fi input with the URL the board
// reports. The work is in Mouthtrap.Firmware; this only picks the inputs and shows the results.
public partial class MainWindow
{
    private Task? firmwareTask;
    private CancellationTokenSource? firmwareCancellation;

    private FirmwareVariant SelectedVariant => FirmwareKind.SelectedIndex switch
    {
        1 => FirmwareVariant.XiaoUvc,
        2 => FirmwareVariant.XiaoUvcMultimode,
        _ => FirmwareVariant.XiaoWifi,
    };

    // The Wi-Fi input's token file when one is chosen; otherwise a new one next to the settings file.
    private string FirmwareTokenPath => tokenFilePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "stream-token.txt");

    private void FirmwareKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FirmwareWifiOptions is null) return;
        FirmwareWifiOptions.Visibility = SelectedVariant == FirmwareVariant.XiaoWifi ? Visibility.Visible : Visibility.Collapsed;
        ShowFirmwareHint();
        UpdateFirmwareControls();
    }

    private void FirmwarePorts_Click(object sender, RoutedEventArgs e) => RefreshFirmwarePorts();
    private void FirmwarePort_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFirmwareControls();
    private void FirmwareField_Changed(object sender, RoutedEventArgs e) => UpdateFirmwareControls();

    private void RefreshFirmwarePorts()
    {
        var selected = FirmwarePort.SelectedItem as string;
        var ports = BoardLink.SerialPorts();
        FirmwarePort.ItemsSource = ports;
        FirmwarePort.SelectedItem = ports.Contains(selected) ? selected : ports.Length == 1 ? ports[0] : null;
        if (firmwareCancellation is null) FirmwareStatusText.Text = Strings.Get(ports.Length == 0 ? "FirmwareNoPorts" : "FirmwareIdle");
        UpdateFirmwareControls();
    }

    private void ShowFirmwareHint() => FirmwareHint.Text = SelectedVariant == FirmwareVariant.XiaoWifi
        ? Strings.Format("FirmwareHintWifi", FirmwareTokenPath)
        : Strings.Get("FirmwareHintUvc");

    private void UpdateFirmwareControls()
    {
        if (FirmwareWriteButton is null) return;
        var running = firmwareCancellation is not null;
        var idle = !closing && !running && sessionCancellation is null;
        FirmwareKind.IsEnabled = FirmwarePort.IsEnabled = FirmwarePortsButton.IsEnabled = FirmwareWifiOptions.IsEnabled = idle;
        FirmwareWriteButton.Content = Strings.Get(running ? "FirmwareCancelButton" : "FirmwareWriteButton");
        FirmwareWriteButton.IsEnabled = running ? !firmwareCancellation!.IsCancellationRequested && !closing
            : idle && FirmwarePort.SelectedItem is string && (SelectedVariant != FirmwareVariant.XiaoWifi ||
                (FirmwareSsid.Text.Length > 0 && FirmwarePassword.Password.Length > 0));
        FirmwareProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FirmwareWrite_Click(object sender, RoutedEventArgs e)
    {
        if (firmwareCancellation is { } running)
        {
            running.Cancel();
            UpdateFirmwareControls();
            return;
        }
        if (closing || sessionCancellation is not null || FirmwarePort.SelectedItem is not string port) return;
        var variant = SelectedVariant;
        WifiSettings? wifi = null;
        if (variant == FirmwareVariant.XiaoWifi)
        {
            var read = StreamToken.ReadOrCreate(FirmwareTokenPath);
            if (!read.TryGetOk(out var token))
            {
                FirmwareStatusText.Text = Strings.Format("FirmwareTokenInvalid", FirmwareTokenPath, read.TryGetErr(out var error) ? error!.Message : "");
                return;
            }
            switch (WifiSettings.Create(FirmwareSsid.Text, FirmwarePassword.Password, token!))
            {
                case Ok<WifiSettings>(var settings):
                    wifi = settings;
                    // The password stays in memory only for this write.
                    FirmwarePassword.Clear();
                    break;
                case Err<InvalidValue>(var invalid):
                    FirmwareStatusText.Text = Strings.Get(invalid.Name == nameof(WifiSettings.Ssid) ? "FirmwareSsidInvalid" : "FirmwarePasswordInvalid");
                    return;
            }
        }
        firmwareCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateControls();
        firmwareTask = WriteFirmwareAsync(variant, port, wifi, firmwareCancellation.Token);
        await firmwareTask;
    }

    private async Task WriteFirmwareAsync(FirmwareVariant variant, string port, WifiSettings? wifi, CancellationToken cancellationToken)
    {
        var environment = new FirmwareEnvironment(clock);
        try
        {
            FirmwareProgress.Value = 0;
            FirmwareStatusText.Text = Strings.Get("FirmwareDownloading");
            var download = await new FirmwareRelease().Download(variant).RunExitAsync(environment, cancellationToken);
            if (download is not Ok<FirmwareDownload>(var firmware))
            {
                FirmwareStatusText.Text = download switch
                {
                    Err<ReleaseError>(ReleaseUnavailable(var message, _)) => Strings.Format("FirmwareReleaseUnavailable", message),
                    Err<ReleaseError>(ReleaseInvalid(var message)) => Strings.Format("FirmwareReleaseInvalid", message),
                    Die die => ReportDefect(die),
                    _ => Strings.Get("FirmwareCancelled"),
                };
                return;
            }
            var plan = firmware.Plan;
            if (wifi is not null)
            {
                // The release's partition table puts nvs at NvsImage.Offset, so a plan it refuses is a broken release.
                if (plan.With(new(NvsImage.Offset, NvsImage.Create(wifi), "Wi-Fi settings")) is not Ok<FlashPlan>(var withSettings))
                {
                    FirmwareStatusText.Text = Strings.Format("FirmwareReleaseInvalid", "The release leaves no room for the Wi-Fi settings.");
                    return;
                }
                plan = withSettings;
            }
            var progress = new Progress<FlashProgress>(report => ShowFirmwareProgress(report, port, wifi));
            var exit = await BoardInstaller.Install(BoardLink.Open(port), plan, watchCamera: wifi is not null, progress)
                .RunExitAsync(environment, cancellationToken);
            FirmwareStatusText.Text = exit switch
            {
                Ok<InstallReport>(var report) => FirmwareResult(firmware.Version, variant, report),
                Err<FlashError>(PortUnavailable(var message)) => Strings.Format("FirmwarePortUnavailable", message),
                Err<FlashError>(BoardNotResponding) => Strings.Get("FirmwareNotResponding"),
                Err<FlashError>(WrongChip(var message)) => Strings.Format("FirmwareWrongChip", message),
                Err<FlashError>(BoardRejected(var message, _, _)) => Strings.Format("FirmwareRejected", message),
                Err<FlashError>(ConnectionLost(var message)) => Strings.Format("FirmwareLost", message),
                Err<FlashError>(VerificationFailed(var message)) => Strings.Format("FirmwareVerifyFailed", message),
                Die die => ReportDefect(die),
                Interrupted => Strings.Get("FirmwareCancelled"),
            };
        }
        finally
        {
            firmwareCancellation?.Dispose();
            firmwareCancellation = null;
            UpdateControls();
        }
    }

    private void ShowFirmwareProgress(FlashProgress report, string port, WifiSettings? wifi)
    {
        if (firmwareCancellation is null) return;
        FirmwareProgress.Value = report.Total == 0 ? 0 : (double)report.Written / report.Total;
        FirmwareStatusText.Text = report.Stage switch
        {
            FlashStage.Connecting => Strings.Format("FirmwareConnecting", port),
            FlashStage.Preparing => Strings.Get("FirmwarePreparing"),
            FlashStage.Writing => Strings.Format("FirmwareWriting", report.Part, FirmwareProgress.Value),
            FlashStage.Restarting => Strings.Get("FirmwareRestarting"),
            FlashStage.WaitingForCamera => Strings.Format("FirmwareWaitingCamera", wifi?.Ssid),
            _ => throw new ArgumentOutOfRangeException(nameof(report), report.Stage, null),
        };
    }

    // What to do next. For the Wi-Fi firmware, the Wi-Fi input gets the token file, and the URL when the board
    // reported one.
    private string FirmwareResult(string version, FirmwareVariant variant, InstallReport report)
    {
        if (!report.Restarted) return Strings.Format("FirmwareDoneNoRestart", version);
        if (variant != FirmwareVariant.XiaoWifi) return Strings.Format("FirmwareDoneUvc", version);
        tokenFilePath = FirmwareTokenPath;
        TokenFileText.Text = tokenFilePath;
        InputKind.SelectedIndex = 3;
        switch (report.Camera)
        {
            case Ok<StreamUrl>(var url):
                MjpegUrl.Text = url.ToString();
                SaveSettings();
                return Strings.Format("FirmwareDoneWifi", version, url);
            case Err<StartupError>(FirmwareFailed(var message)):
                SaveSettings();
                return Strings.Format("FirmwareDoneWifiFailed", version, message);
            case Err<StartupError>(CameraUrlNotSeen(var message)):
                SaveSettings();
                return Strings.Format("FirmwareDoneWifiNotSeen", version, message);
            default:
                SaveSettings();
                return Strings.Format("FirmwareDoneNoRestart", version);
        }
    }
}
