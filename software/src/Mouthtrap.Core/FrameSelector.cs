namespace Mouthtrap.Core;

// One frame for the stages to process. SettingsChanged is true when Settings starts a new revision, which restarts
// calibration.
public readonly record struct FrameSelection(Rgb24FrameImage Image, long ReceivedAt, TrackingControlState Settings,
    bool SettingsChanged);

// Decides what the tracking loop processes next: a frame that arrived, or the held frame again when the settings
// change, so a still image follows them as well. Owned by the single tracking loop.
public sealed class FrameSelector
{
    private Rgb24FrameImage? held;
    private long heldReceivedAt;
    private int? revision;

    // Null when nothing new arrived and the settings are unchanged.
    public FrameSelection? Select(Rgb24FrameImage? arrived, long receivedAt, TrackingControlState settings)
    {
        if (arrived is not null)
        {
            held = arrived;
            heldReceivedAt = receivedAt;
        }
        else if (held is null || settings.Revision == revision) return null;
        var changed = settings.Revision != revision;
        revision = settings.Revision;
        return new(held, heldReceivedAt, settings, changed);
    }
}
