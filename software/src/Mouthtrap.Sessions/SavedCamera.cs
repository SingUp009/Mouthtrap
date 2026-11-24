using FlashCap;
using Mouthtrap.Core;

namespace Mouthtrap.Sessions;

// Finds the saved camera and capture mode again among the connected ones, the same way in the GUI and the CLI.
public static class SavedCamera
{
    // The index of the saved camera: the device with the saved identity (its device path), else the first with the
    // saved name, as a camera plugged into another port gets a new path. -1 when neither is connected.
    public static int IndexOf(IReadOnlyList<CaptureDeviceDescriptor> devices, CameraSettings saved)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(saved);
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].Identity.ToString() == saved.Identity) return i;
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].Name == saved.Name) return i;
        return -1;
    }

    // The saved capture mode among the device's supported modes, when the device is the saved camera as IndexOf finds
    // it (the same identity, or the same name after it moved to another port). Null when it is another camera, no mode
    // was saved, or the device no longer offers the saved mode.
    public static VideoCharacteristics? ModeOf(CaptureDeviceDescriptor device, CameraSettings saved)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(saved);
        if (IndexOf([device], saved) != 0 || saved.Mode is not { } mode) return null;
        return device.Characteristics.FirstOrDefault(item => item.PixelFormat != PixelFormats.Unknown && Matches(item, mode));
    }

    // A mode is the saved one when its size and format are the same and its rate is within 0.05 fps.
    public static bool Matches(VideoCharacteristics mode, CameraModeSettings saved)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(saved);
        return mode.Width == saved.Width && mode.Height == saved.Height && mode.PixelFormat.ToString() == saved.PixelFormat &&
            Math.Abs((double)mode.FramesPerSecond - saved.Fps) <= 0.05;
    }
}
