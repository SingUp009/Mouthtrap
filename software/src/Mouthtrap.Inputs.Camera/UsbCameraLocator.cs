using FlashCap;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

// Waits for a USB camera that a device has just become, such as the XIAO restarting as the webcam UVC CAM1, by
// listing the cameras once a second. A listing that fails while the device re-enumerates is tried again.
public sealed class UsbCameraLocator : IUsbCameraLocator
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, ValueTask<string[]?>> names;

    public UsbCameraLocator() : this(ListNames) { }

    // names lists the camera names, or null when the listing fails; tests replace the system's cameras.
    internal UsbCameraLocator(Func<CancellationToken, ValueTask<string[]?>> names) => this.names = names;

    public async ValueTask<bool> WaitForAsync(string name, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var deadline = clock.GetTimestamp() + (long)(timeout.TotalSeconds * clock.TimestampFrequency);
        while (true)
        {
            if ((await names(cancellationToken).ConfigureAwait(false))?.Contains(name, StringComparer.Ordinal) == true) return true;
            if (clock.GetTimestamp() >= deadline) return false;
            await Task.Delay(Interval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<string[]?> ListNames(CancellationToken cancellationToken)
    {
        var exit = await CameraDiagnostics.Enumerate<CameraEnvironment>().OnThreadPool()
            .RunExitAsync(new CameraEnvironment(new CaptureDevices()), cancellationToken).ConfigureAwait(false);
        if (exit is Interrupted) cancellationToken.ThrowIfCancellationRequested();
        // A defect stays a defect; only a typed listing failure is tried again.
        if (exit is Die die) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(die.Exception);
        return exit is Ok<CaptureDeviceDescriptor[]>(var devices) ? [.. devices.Select(device => device.Name)] : null;
    }
}
