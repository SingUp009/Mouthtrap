using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Core;

// Input failures are classified by what the user can do about them, not by the kind of input.
// The selection or settings are wrong; change them before starting again.
public sealed record InputSettingsInvalid(string Message);
// The input could not be opened or started (missing, busy, denied, unreachable).
public sealed record InputUnavailable(string Message, int? Code = null);
// The input started and then stopped delivering frames (disconnected or stalled).
public sealed record InputLost(string Message);
// The input delivered content that cannot be read as frames.
public sealed record InputDataInvalid(string Message);

[Error]
public partial union FrameSourceError(InputSettingsInvalid, InputUnavailable, InputLost, InputDataInvalid);

// What every tracking session runs with, in the GUI and the CLI alike: the clock for timestamps, timeouts and waits.
[Moderato.Effects.Environment]
public sealed partial record TrackingEnvironment(TimeProvider Clock);

// Where an input publishes what it captures. Publishing transfers ownership: the input must not mutate the image
// afterwards. Safe to call from any thread; what is kept and for how long is the pipeline's choice.
public interface IFrameWriter
{
    void Publish(FrameImage ownedImage, long receivedAt);
}

public interface IFrameSource
{
    // Publishes owned images until the input ends; cancellation must await input release.
    Effect<Unit, FrameSourceError, TrackingEnvironment> Run(IFrameWriter frames);
}
