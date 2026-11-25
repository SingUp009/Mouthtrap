using Moderato.Effects;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// The Wi-Fi settings the board stores, and the file where Mouthtrap keeps the same stream token.
public sealed record WifiSetup(WifiSettings Settings, string TokenFile);

// Sets up a Seeed Studio XIAO ESP32S3 Sense on a serial port: writes a Mouthtrap firmware through the ESP32-S3 ROM
// loader and, for the Wi-Fi camera, the network and the stream token; then reports how its video arrives. Wifi is
// required for the Wi-Fi camera and refused for the webcams.
public sealed record XiaoProvisioner(FirmwareVariant Variant, IFirmwareSource Firmware, string Port, WifiSetup? Wifi = null) : IDeviceProvisioner
{
    // The name the USB webcam firmware gives the camera (CONFIG_UVC_CAM1 in firmware/xiao-uvc).
    public const string UsbCameraName = "UVC CAM1";

    // The connection to the board; tests replace it.
    internal Func<string, Resource<IBoardLink, FlashError, ProvisioningEnvironment>> OpenBoard { get; init; } = BoardLink.Open;

    public Effect<ProvisionedDevice, ProvisioningError, ProvisioningEnvironment> Provision(IProgress<ProvisioningProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(Firmware);
        ArgumentException.ThrowIfNullOrWhiteSpace(Port);
        if ((Variant == FirmwareVariant.XiaoWifi) != (Wifi is not null))
            throw new ArgumentException("The Wi-Fi camera needs Wi-Fi settings, and the webcams take none.", nameof(Wifi));
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
            var report = await scope.Run(BoardInstaller.Install(OpenBoard(Port), plan, watchCamera: Wifi is not null,
                    progress is null ? null : new Progress(progress))
                .MapErr<ProvisioningError>(Classify));
            return new ProvisionedDevice(firmware.Version, Input(report));
        });
    }

    private ProvisionedInput Input(InstallReport report)
    {
        var token = Wifi?.TokenFile;
        if (!report.Restarted)
            return new InputNotConfirmed(InputNotConfirmedReason.RestartByHand, "The board could not be restarted from here; press its RESET button.", token);
        if (Wifi is null) return new UsbCameraReady(UsbCameraName);
        return report.Camera switch
        {
            Moderato.Results.Ok<StreamUrl>(var address) => new MjpegStreamReady(address, Wifi.TokenFile),
            Moderato.Results.Err<StartupError>(FirmwareFailed(var message)) => new InputNotConfirmed(InputNotConfirmedReason.DeviceReportedProblem, message, token),
            Moderato.Results.Err<StartupError>(CameraUrlNotSeen(var message)) => new InputNotConfirmed(InputNotConfirmedReason.NotReported, message, token),
            _ => throw new InvalidOperationException("The Wi-Fi camera's start was not watched."),
        };
    }

    // ESP32-S3 flashing errors in the terms every device shares.
    private static ProvisioningError Classify(FlashError error) => error switch
    {
        PortUnavailable(var message) => new DeviceUnavailable(message),
        BoardNotResponding(var message) => new DeviceNotResponding(message),
        WrongChip(var message) => new DeviceIncompatible(message),
        BoardRejected(var message, _, _) => new DeviceWriteFailed(message),
        ConnectionLost(var message) => new DeviceWriteFailed(message),
        VerificationFailed(var message) => new DeviceWriteFailed(message),
    };

    // Flashing progress as setup progress, reported synchronously so that the order is kept.
    private sealed class Progress(IProgress<ProvisioningProgress> target) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => target.Report(new(value.Stage switch
        {
            FlashStage.Connecting or FlashStage.Preparing => ProvisioningStage.Connecting,
            FlashStage.Writing => ProvisioningStage.Writing,
            FlashStage.Restarting => ProvisioningStage.Restarting,
            FlashStage.WaitingForCamera => ProvisioningStage.WaitingForInput,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Stage, null),
        }, value.Total == 0 ? 0 : (double)value.Written / value.Total, value.Part));
    }
}
