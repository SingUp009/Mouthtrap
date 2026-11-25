namespace Mouthtrap.Core;

// Confirms a device without asking, for hosts that cannot ask, such as the CLI an agent drives: only the device whose
// identity was read beforehand and passed in, and only one the firmware can run on. Reported is the last report seen,
// for the host to show what was found when it declines.
public sealed class ExpectedDeviceConfirmation(string identity) : IDeviceConfirmation
{
    public DeviceReport? Reported { get; private set; }

    public ValueTask<bool> ConfirmAsync(DeviceReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        Reported = report;
        return ValueTask.FromResult(report.CanBeWritten && string.Equals(report.Identity, identity.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
