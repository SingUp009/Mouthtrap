using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// The Wi-Fi settings the board stores, and the file where Mouthtrap keeps the same stream token.
public sealed record WifiSetup(WifiSettings Settings, string TokenFile);

// Sets up a Seeed Studio XIAO ESP32S3 Sense on a serial port: writes a Mouthtrap firmware through the ESP32-S3 ROM
// loader and, for the Wi-Fi camera, the network and the stream token; then reports how its video arrives. Wifi is
// required for the Wi-Fi camera and refused for the webcams; Cameras, which finds the webcam once Windows lists it, is
// required for the webcams.
public sealed record XiaoProvisioner(FirmwareVariant Variant, IFirmwareSource Firmware, string Port, WifiSetup? Wifi = null) : IDeviceProvisioner
{
    // The name the USB webcam firmware gives the camera (CONFIG_UVC_CAM1 in firmware/xiao-uvc).
    public const string UsbCameraName = "UVC CAM1";
    public static readonly TimeSpan UsbCameraTimeout = TimeSpan.FromSeconds(30);

    public IUsbCameraLocator? Cameras { get; init; }

    // The connection to the board, and the PSRAM the board must report (null accepts any, as for Espressif's QEMU,
    // whose eFuses are blank); tests replace them.
    internal Func<string, Resource<IBoardLink, FlashError, ProvisioningEnvironment>> OpenBoard { get; init; } = BoardLink.Open;
    internal long? RequiredPsramBytes { get; init; } = XiaoBoard.PsramBytes;

    public Effect<ProvisionedDevice, ProvisioningError, ProvisioningEnvironment> Provision(IDeviceConfirmation confirmation,
        IProgress<ProvisioningProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(Firmware);
        ArgumentException.ThrowIfNullOrWhiteSpace(Port);
        if ((Variant == FirmwareVariant.XiaoWifi) != (Wifi is not null))
            throw new ArgumentException("The Wi-Fi camera needs Wi-Fi settings, and the webcams take none.", nameof(Wifi));
        if (Variant != FirmwareVariant.XiaoWifi && Cameras is null)
            throw new ArgumentException("The webcams need a camera locator to confirm the camera.", nameof(Cameras));
        return Scope<ProvisioningError, ProvisioningEnvironment>.Gen(async scope =>
        {
            progress?.Report(new(ProvisioningStage.LoadingFirmware, 0));
            var firmware = await scope.Run(Firmware.Load(Variant));
            var plan = firmware.Plan;
            if (Wifi is not null)
            {
                // A partition table that puts the app over nvs refuses the settings; that firmware is not Mouthtrap's.
                if (!plan.With(new(NvsImage.Offset, NvsImage.Create(Wifi.Settings), "Wi-Fi settings")).TryGetOk(out var withSettings))
                    throw scope.Fail(new FirmwareInvalid("The firmware leaves no room for the Wi-Fi settings at 0x9000."));
                plan = withSettings!;
            }
            var report = await scope.Run(BoardInstaller.Install(OpenBoard(Port), plan, (board, token) => Check(board, plan, confirmation, token),
                    watchCamera: Wifi is not null, progress is null ? null : new Progress(progress))
                .MapErr<ProvisioningError>(XiaoBoard.Classify));
            var input = Input(report);
            if (input is UsbCameraReady(var name))
            {
                progress?.Report(new(ProvisioningStage.WaitingForInput, 1));
                var clock = scope.Env.Resolve<TimeProvider>();
                if (!await Cameras!.WaitForAsync(name, UsbCameraTimeout, clock, scope.CancellationToken))
                    input = new InputNotConfirmed(InputNotConfirmedReason.NotReported,
                        $"Windows did not list the camera {name} within {UsbCameraTimeout.TotalSeconds:0} seconds. Unplug the board and plug it in again.");
            }
            return new ProvisionedDevice(firmware.Version, input);
        });
    }

    private async ValueTask<Result<Unit, FlashError>> Check(Esp32S3Board board, FlashPlan plan, IDeviceConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        var report = XiaoBoard.Describe(board, plan.FlashSize, RequiredPsramBytes);
        if (!report.CanBeWritten)
            return new Err<FlashError>(new BoardUnsuitable(string.Join(" ", report.Problems.Select(problem => problem.Message))));
        return await confirmation.ConfirmAsync(report, cancellationToken).ConfigureAwait(false)
            ? new Ok<Unit>(Unit.Value)
            : new Err<FlashError>(new NotConfirmed($"The board {report.Identity} was not confirmed, so nothing was written."));
    }

    private ProvisionedInput Input(InstallReport report)
    {
        var token = Wifi?.TokenFile;
        if (!report.Restarted)
            return new InputNotConfirmed(InputNotConfirmedReason.RestartByHand, "The board could not be restarted from here; press its RESET button.", token);
        if (Wifi is null) return new UsbCameraReady(UsbCameraName);
        return report.Camera switch
        {
            Ok<StreamUrl>(var address) => new MjpegStreamReady(address, Wifi.TokenFile),
            Err<StartupError>(FirmwareFailed(var message)) => new InputNotConfirmed(InputNotConfirmedReason.DeviceReportedProblem, message, token),
            Err<StartupError>(CameraUrlNotSeen(var message)) => new InputNotConfirmed(InputNotConfirmedReason.NotReported, message, token),
            _ => throw new InvalidOperationException("The Wi-Fi camera's start was not watched."),
        };
    }

    // Flashing progress as setup progress, reported synchronously so that the order is kept.
    private sealed class Progress(IProgress<ProvisioningProgress> target) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => target.Report(new(value.Stage switch
        {
            FlashStage.Connecting or FlashStage.Preparing => ProvisioningStage.Connecting,
            FlashStage.Confirming => ProvisioningStage.Confirming,
            FlashStage.Writing => ProvisioningStage.Writing,
            FlashStage.Restarting => ProvisioningStage.Restarting,
            FlashStage.WaitingForCamera => ProvisioningStage.WaitingForInput,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Stage, null),
        }, value.Total == 0 ? 0 : (double)value.Written / value.Total, value.Part));
    }
}
