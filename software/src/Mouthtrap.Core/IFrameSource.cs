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

[Moderato.Effects.Environment]
public sealed partial record PreviewEnvironment(TimeProvider Clock);

public interface IFrameSource
{
    // Publish owned images and observe frames.Failure; cancellation must await input release.
    Effect<Unit, FrameSourceError, PreviewEnvironment> Run(LatestFrameBuffer frames);
}
