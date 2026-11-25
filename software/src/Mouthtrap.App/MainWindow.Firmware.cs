using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;
using Mouthtrap.Inputs;

namespace Mouthtrap.App;

// The Board firmware card: sets a XIAO up with the chosen firmware (the latest published release, a release
// version, or a zip file) and, for the Wi-Fi camera, its network and stream token; then sets up the input that the
// device reports. Setting a device up is IDeviceProvisioner's work; this only picks the inputs and shows the results.
public partial class MainWindow
{
    private Task? firmwareTask;
    private CancellationTokenSource? firmwareCancellation;
    private FirmwareChoice? fileChoice;
    private IReadOnlyList<ReleaseVersion> releases = [];

    // One entry of the firmware list; the list shows Label.
    private sealed record FirmwareChoice(string Label, IFirmwareSource Source)
    {
        public override string ToString() => Label;
    }

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
    private void FirmwareSource_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFirmwareControls();
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

    // The latest published release, then the releases found on GitHub, then the chosen zip file.
    private void ShowFirmwareChoices(FirmwareChoice? select = null)
    {
        var selected = select ?? FirmwareSource.SelectedItem as FirmwareChoice;
        List<FirmwareChoice> choices = [new(Strings.Get("FirmwareLatest"), new FirmwareRelease())];
        choices.AddRange(releases.Select(release => new FirmwareChoice(
            Strings.Format(release.Prerelease ? "FirmwarePrerelease" : "FirmwareRelease", release.Version), new FirmwareRelease(release.Version))));
        if (fileChoice is not null) choices.Add(fileChoice);
        FirmwareSource.ItemsSource = choices;
        FirmwareSource.SelectedItem = choices.FirstOrDefault(choice => choice == selected) ?? choices[0];
    }

    private async void FirmwareReleases_Click(object sender, RoutedEventArgs e)
    {
        if (firmwareCancellation is not null || closing) return;
        firmwareCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateControls();
        firmwareTask = FindReleasesAsync(firmwareCancellation.Token);
        await firmwareTask;
    }

    private async Task FindReleasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            FirmwareStatusText.Text = Strings.Get("FirmwareFindingReleases");
            var exit = await FirmwareRelease.Releases().RunExitAsync(new ProvisioningEnvironment(clock), cancellationToken);
            FirmwareStatusText.Text = exit switch
            {
                Ok<IReadOnlyList<ReleaseVersion>>(var found) => ShowReleases(found),
                Err<ProvisioningError>(var error) => ProvisioningErrorText(error),
                Die die => ReportDefect(die),
                Interrupted => Strings.Get("FirmwareIdle"),
            };
        }
        finally
        {
            firmwareCancellation?.Dispose();
            firmwareCancellation = null;
            UpdateControls();
        }
    }

    private string ShowReleases(IReadOnlyList<ReleaseVersion> found)
    {
        releases = found;
        ShowFirmwareChoices();
        return found.Count == 0 ? Strings.Get("FirmwareNoReleases") : Strings.Format("FirmwareReleasesFound", found.Count);
    }

    private void FirmwareFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Strings.Get("FirmwareFileDialogTitle"), Filter = Strings.Get("FirmwareFileDialogFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        fileChoice = new(Strings.Format("FirmwareFileChoice", Path.GetFileName(dialog.FileName)), new FirmwareFile(dialog.FileName));
        ShowFirmwareChoices(fileChoice);
    }

    private void ShowFirmwareHint() => FirmwareHint.Text = SelectedVariant == FirmwareVariant.XiaoWifi
        ? Strings.Format("FirmwareHintWifi", FirmwareTokenPath)
        : Strings.Get("FirmwareHintUvc");

    private void UpdateFirmwareControls()
    {
        if (FirmwareWriteButton is null) return;
        var running = firmwareCancellation is not null;
        var idle = !closing && !running && sessionCancellation is null;
        FirmwareKind.IsEnabled = FirmwareSource.IsEnabled = FirmwareReleasesButton.IsEnabled = FirmwareFileButton.IsEnabled = idle;
        FirmwarePort.IsEnabled = FirmwarePortsButton.IsEnabled = FirmwareWifiOptions.IsEnabled = idle;
        FirmwareWriteButton.Content = Strings.Get(running ? "FirmwareCancelButton" : "FirmwareWriteButton");
        FirmwareWriteButton.IsEnabled = running ? !firmwareCancellation!.IsCancellationRequested && !closing
            : idle && FirmwareSource.SelectedItem is FirmwareChoice && FirmwarePort.SelectedItem is string &&
                (SelectedVariant != FirmwareVariant.XiaoWifi || (FirmwareSsid.Text.Length > 0 && FirmwarePassword.Password.Length > 0));
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
        if (closing || sessionCancellation is not null || FirmwarePort.SelectedItem is not string port ||
            FirmwareSource.SelectedItem is not FirmwareChoice choice) return;
        var variant = SelectedVariant;
        WifiSetup? wifi = null;
        if (variant == FirmwareVariant.XiaoWifi)
        {
            var tokenPath = FirmwareTokenPath;
            var read = StreamToken.ReadOrCreate(tokenPath);
            if (!read.TryGetOk(out var token))
            {
                FirmwareStatusText.Text = Strings.Format("FirmwareTokenInvalid", tokenPath, read.TryGetErr(out var error) ? error!.Message : "");
                return;
            }
            switch (WifiSettings.Create(FirmwareSsid.Text, FirmwarePassword.Password, token!))
            {
                case Ok<WifiSettings>(var settings):
                    wifi = new(settings, tokenPath);
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
        var provisioner = new XiaoProvisioner(variant, choice.Source, port, wifi)
        {
            Cameras = variant == FirmwareVariant.XiaoWifi ? null : new UsbCameraLocator(),
        };
        var firmware = (string)((ComboBoxItem)FirmwareKind.SelectedItem).Content;
        firmwareTask = ProvisionAsync(provisioner, new BoardConfirmation(this, port, firmware, choice.Label, wifi is not null), wifi?.Settings.Ssid,
            firmwareCancellation.Token);
        await firmwareTask;
    }

    // Shows what the board reported and asks before anything is written. The provisioner keeps the board connected
    // meanwhile; the question is asked on the window's thread.
    private sealed class BoardConfirmation(MainWindow window, string port, string firmware, string version, bool wifi) : IDeviceConfirmation
    {
        public async ValueTask<bool> ConfirmAsync(DeviceReport report, CancellationToken cancellationToken) =>
            await window.Dispatcher.InvokeAsync(() => MessageBox.Show(window,
                Strings.Format("FirmwareConfirmText", port, report.Chip, Size(report.FlashBytes), Size(report.MemoryBytes), report.Identity, firmware,
                    version) + (wifi ? Strings.Get("FirmwareConfirmWifi") : ""),
                Strings.Get("FirmwareConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,
                System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);

        private static string Size(long? bytes) => bytes is { } value
            ? Strings.Format("FirmwareMegabytes", value / 1024.0 / 1024)
            : Strings.Get("FirmwareNone");
    }

    private async Task ProvisionAsync(IDeviceProvisioner provisioner, IDeviceConfirmation confirmation, string? network,
        CancellationToken cancellationToken)
    {
        try
        {
            FirmwareProgress.Value = 0;
            var progress = new Progress<ProvisioningProgress>(report => ShowProvisioningProgress(report, network));
            var exit = await provisioner.Provision(confirmation, progress).RunExitAsync(new ProvisioningEnvironment(clock), cancellationToken);
            // The webcam is now a camera Windows lists: list the cameras again so that it can be selected as the input.
            if (exit is Ok<ProvisionedDevice>({ Input: UsbCameraReady }) && !closing) await RefreshAsync();
            FirmwareStatusText.Text = exit switch
            {
                Ok<ProvisionedDevice>(var device) => UseProvisionedInput(device),
                Err<ProvisioningError>(var error) => ProvisioningErrorText(error),
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

    private void ShowProvisioningProgress(ProvisioningProgress report, string? network)
    {
        if (firmwareCancellation is null) return;
        FirmwareProgress.Value = report.Fraction;
        FirmwareStatusText.Text = report.Stage switch
        {
            ProvisioningStage.LoadingFirmware => Strings.Get("FirmwareLoading"),
            ProvisioningStage.Connecting => Strings.Get("FirmwareConnecting"),
            ProvisioningStage.Confirming => Strings.Get("FirmwareConfirming"),
            ProvisioningStage.Writing => Strings.Format("FirmwareWriting", report.Detail, report.Fraction),
            ProvisioningStage.Restarting => Strings.Get("FirmwareRestarting"),
            ProvisioningStage.WaitingForInput => Strings.Format("FirmwareWaitingCamera", network),
            _ => throw new ArgumentOutOfRangeException(nameof(report), report.Stage, null),
        };
    }

    // Sets up the input the device reported and says what to do next. A transport the GUI has no input for is a
    // defect here: a new transport adds its IFrameSource, its ProvisionedInput and a case below.
    private string UseProvisionedInput(ProvisionedDevice device)
    {
        var version = device.FirmwareVersion;
        switch (device.Input)
        {
            case MjpegStreamReady(var address, var tokenFile):
                UseStreamInput(tokenFile, address);
                return Strings.Format("FirmwareDoneWifi", version, address);
            case UsbCameraReady(var name):
                return UseCameraInput(name)
                    ? Strings.Format("FirmwareDoneUvc", version, name)
                    : Strings.Format("FirmwareDoneUvcNotListed", version, name);
            case InputNotConfirmed(var reason, var message, var tokenFile):
                if (tokenFile is not null) UseStreamInput(tokenFile, null);
                return reason switch
                {
                    InputNotConfirmedReason.RestartByHand => Strings.Format("FirmwareDoneNoRestart", version),
                    InputNotConfirmedReason.DeviceReportedProblem => Strings.Format("FirmwareDoneWifiFailed", version, message),
                    InputNotConfirmedReason.NotReported => Strings.Format("FirmwareDoneWifiNotSeen", version, message),
                    _ => throw new ArgumentOutOfRangeException(nameof(device), reason, null),
                };
            default:
                throw new InvalidOperationException($"No input is set up for {device.Input.GetType().Name}.");
        }
    }

    // Selects the camera named name as the input, once the camera list holds it.
    private bool UseCameraInput(string name)
    {
        if (Cameras.ItemsSource is not IEnumerable<FlashCap.CaptureDeviceDescriptor> devices ||
            devices.FirstOrDefault(device => device.Name == name) is not { } camera) return false;
        InputKind.SelectedIndex = 0;
        Cameras.SelectedItem = camera;
        SaveSettings();
        return true;
    }

    private void UseStreamInput(string tokenFile, StreamUrl? address)
    {
        tokenFilePath = tokenFile;
        TokenFileText.Text = tokenFile;
        InputKind.SelectedIndex = 3;
        if (address is not null) MjpegUrl.Text = address.ToString();
        ShowFirmwareHint();
        SaveSettings();
    }

    private static string ProvisioningErrorText(ProvisioningError error) => error switch
    {
        FirmwareUnavailable(var message, _) => Strings.Format("FirmwareReleaseUnavailable", message),
        FirmwareInvalid(var message) => Strings.Format("FirmwareReleaseInvalid", message),
        DeviceUnavailable(var message) => Strings.Format("FirmwarePortUnavailable", message),
        DeviceNotResponding => Strings.Get("FirmwareNotResponding"),
        DeviceIncompatible(var message) => Strings.Format("FirmwareWrongChip", message),
        DeviceNotConfirmed => Strings.Get("FirmwareNotConfirmed"),
        DeviceWriteFailed(var message) => Strings.Format("FirmwareLost", message),
    };
}
