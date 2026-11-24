namespace Mouthtrap.Core;

public sealed record PreprocessSettings
{
    public static PreprocessSettings Default { get; } = new(CropRegion.FullFrame, QuarterTurn.None, false);
    public CropRegion Crop { get; }
    public QuarterTurn Rotation { get; }
    public bool Mirror { get; }

    public PreprocessSettings(CropRegion crop, QuarterTurn rotation, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (!Enum.IsDefined(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
        Crop = crop;
        Rotation = rotation;
        Mirror = mirror;
    }
}

public readonly record struct TrackingControlState(PreprocessSettings Preprocessing, int Revision);

// Hosts change settings from any thread; every change starts a new revision, which restarts calibration
// and makes the pipeline reprocess the frame it holds.
public sealed class TrackingControl
{
    private readonly object gate = new();
    private TrackingControlState current;

    // Raised after each new revision, on the thread that made it.
    public event Action? Changed;

    public TrackingControl(PreprocessSettings? preprocessing = null) =>
        current = new(preprocessing ?? PreprocessSettings.Default, 0);

    public TrackingControlState Current
    {
        get { lock (gate) return current; }
    }

    public int Update(PreprocessSettings preprocessing)
    {
        ArgumentNullException.ThrowIfNull(preprocessing);
        int revision;
        lock (gate)
        {
            current = new(preprocessing, current.Revision + 1);
            revision = current.Revision;
        }
        Changed?.Invoke();
        return revision;
    }

    public int ResetCalibration()
    {
        int revision;
        lock (gate)
        {
            current = current with { Revision = current.Revision + 1 };
            revision = current.Revision;
        }
        Changed?.Invoke();
        return revision;
    }
}
