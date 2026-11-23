using FlashCap;

namespace Mouthtrap.Diagnostics;

public static class CameraReport
{
    public static bool IsPreviewCandidate(VideoCharacteristics mode) =>
        mode.PixelFormat != PixelFormats.Unknown && mode.IsDiscrete &&
        mode.Width == 1280 && mode.Height == 720 &&
        Math.Abs((double)mode.FramesPerSecond - 30) <= 0.05;

    public static int Write(TextWriter output, CaptureDeviceDescriptor[] devices)
    {
        output.WriteLine($"Capture devices: {devices.Length}");
        output.WriteLine("Listed FPS is a driver-advertised value, not measured capture FPS.");
        output.WriteLine("* = 1280x720, within 0.05 fps of 30, discrete and supported by FlashCap.");
        foreach (var device in devices)
        {
            output.WriteLine();
            output.WriteLine($"Name: {device.Name}");
            output.WriteLine($"Backend: {device.DeviceType}");
            output.WriteLine($"Identity: {device.Identity}");
            output.WriteLine($"Description: {device.Description}");
            output.WriteLine($"Modes: {device.Characteristics.Length}");
            foreach (var mode in device.Characteristics)
            {
                var candidate = IsPreviewCandidate(mode) ? "*" : " ";
                var supported = mode.PixelFormat != PixelFormats.Unknown;
                output.WriteLine(FormattableString.Invariant(
                    $" {candidate} {mode.Width}x{mode.Height} | {mode.PixelFormat} | {(double)mode.FramesPerSecond:F6} fps ({mode.FramesPerSecond}) | raw={mode.RawPixelFormat} | discrete={mode.IsDiscrete} | supported={supported}"));
            }
            output.WriteLine($"720p/~30fps candidates: {device.Characteristics.Count(IsPreviewCandidate)}");
        }
        if (devices.Length == 0)
            output.WriteLine("No capture devices found. Check the USB connection and Windows camera permissions.");
        return devices.Length == 0 ? 2 : 0;
    }
}
