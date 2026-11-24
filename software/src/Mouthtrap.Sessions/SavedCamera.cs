using FlashCap;
using Mouthtrap.Core;

namespace Mouthtrap.Sessions;

// Finds the saved capture mode again among a connected camera's modes.
public static class SavedCamera
{
    // A mode is the saved one when its size and format are the same and its rate is within 0.05 fps.
    public static bool Matches(VideoCharacteristics mode, CameraModeSettings saved)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(saved);
        return mode.Width == saved.Width && mode.Height == saved.Height && mode.PixelFormat.ToString() == saved.PixelFormat &&
            Math.Abs((double)mode.FramesPerSecond - saved.Fps) <= 0.05;
    }
}
