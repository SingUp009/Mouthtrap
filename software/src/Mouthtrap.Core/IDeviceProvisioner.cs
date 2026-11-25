using Moderato.Effects;

namespace Mouthtrap.Core;

// Setting a device up fails in ways classified by what the user can do, whatever the device.
// The firmware cannot be fetched or read (no network, no such release, a file that cannot be opened).
public sealed record FirmwareUnavailable(string Message, int? Code = null);
// The firmware was found but cannot be used: damaged, not checked, or built for another device.
public sealed record FirmwareInvalid(string Message);
// The connection to the device cannot be opened: it is missing, busy in another program, or access is denied.
public sealed record DeviceUnavailable(string Message);
// The connection opened but the device's updater did not answer; the user has to start it by hand, such as by
// holding a boot button while resetting the device.
public sealed record DeviceNotResponding(string Message);
// The device is not one the firmware can run on, such as one with too little flash or memory.
public sealed record DeviceIncompatible(string Message);
// The user did not confirm the device shown to them; nothing was written.
public sealed record DeviceNotConfirmed(string Message);
// Writing started and failed partway; the device needs another write before it is used.
public sealed record DeviceWriteFailed(string Message);

[Error]
public partial union ProvisioningError(FirmwareUnavailable, FirmwareInvalid, DeviceUnavailable, DeviceNotResponding, DeviceIncompatible,
    DeviceNotConfirmed, DeviceWriteFailed);

// What a provisioner read from a device before writing anything, for the user to check that it is the device they
// mean. Identity names this one device, such as its MAC address; Chip and the sizes are what the device reports.
// Problems are why the firmware cannot run on it: a device with problems is shown but never written. A report cannot
// prove which board design the chip sits on, so the user confirms that.
public sealed record DeviceReport(string Identity, string Chip, long? FlashBytes, long? MemoryBytes, IReadOnlyList<DeviceProblem> Problems)
{
    public bool CanBeWritten => Problems.Count == 0;
}

public sealed record DeviceProblem(DeviceProblemKind Kind, string Message);

public enum DeviceProblemKind
{
    // The flash is smaller than the firmware's layout.
    FlashTooSmall,
    // The flash did not report a size the provisioner knows.
    FlashUnknown,
    // The memory (PSRAM) the firmware needs is missing or of another size.
    MemoryMissing,
}

// Asks the user whether to write the device described in report, while the provisioner keeps the connection open.
// False stops with nothing written.
public interface IDeviceConfirmation
{
    ValueTask<bool> ConfirmAsync(DeviceReport report, CancellationToken cancellationToken);
}

// Finds a USB camera that a device has just become, as the system lists it.
public interface IUsbCameraLocator
{
    // True once the system lists a camera named name; false when it has not within timeout.
    ValueTask<bool> WaitForAsync(string name, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken);
}

// How video arrives from a device once it is set up. Each case names one transport, so that the host can pick the
// IFrameSource for it; a new transport (UDP, a custom USB protocol) adds a case and an IFrameSource, and nothing in
// preprocessing or inference changes.
public abstract record ProvisionedInput;
// An HTTP MJPEG stream that needs the bearer token in TokenFile.
public sealed record MjpegStreamReady(StreamUrl Address, string TokenFile) : ProvisionedInput;
// A USB camera that the system now lists under CameraName.
public sealed record UsbCameraReady(string CameraName) : ProvisionedInput;
// The device is set up, but its input could not be confirmed. TokenFile is set when the device needs one.
public sealed record InputNotConfirmed(InputNotConfirmedReason Reason, string Message, string? TokenFile = null) : ProvisionedInput;

public enum InputNotConfirmedReason
{
    // The device has to be restarted by hand, such as with its reset button.
    RestartByHand,
    // The device reported a problem when it started, such as a network it could not join.
    DeviceReportedProblem,
    // The device did not report its input in time.
    NotReported,
}

public sealed record ProvisionedDevice(string FirmwareVersion, ProvisionedInput Input);

public enum ProvisioningStage { LoadingFirmware, Connecting, Confirming, Writing, Restarting, WaitingForInput }

// Fraction is the share of the firmware written so far, from 0 to 1. Detail names what is being written.
public sealed record ProvisioningProgress(ProvisioningStage Stage, double Fraction, string? Detail = null);

// What setting a device up runs with: the clock for timeouts and for the waits of reset sequences.
[Moderato.Effects.Environment]
public sealed partial record ProvisioningEnvironment(TimeProvider Clock);

// Writes a device's firmware and stores its settings, so that it can then be read by an IFrameSource. It is
// separate from video input: an input assumes a device that already works, and how a device is set up (an ESP32-S3
// through its ROM loader, an RP2350 through its UF2 bootloader) is no concern of capture or inference.
public interface IDeviceProvisioner
{
    // Reads the device, refuses one the firmware cannot run on, and writes only after confirmation accepts the
    // report. Every connection it opens is owned by the run and released however it ends; cancellation stays a
    // cancellation.
    Effect<ProvisionedDevice, ProvisioningError, ProvisioningEnvironment> Provision(IDeviceConfirmation confirmation,
        IProgress<ProvisioningProgress>? progress = null);
}
