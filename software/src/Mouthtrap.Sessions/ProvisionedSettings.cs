using Mouthtrap.Core;

namespace Mouthtrap.Sessions;

// How setting a device up changes the settings that the GUI and the CLI share, so that both end with the same input.
public static class ProvisionedSettings
{
    // The stream token file for a Wi-Fi camera: the one chosen for the Wi-Fi input, or stream-token.txt next to the
    // settings file.
    public static string TokenFile(string settingsPath, string? chosen)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingsPath);
        return chosen ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "stream-token.txt");
    }

    // The settings with the input that the device reports. A stream becomes the Wi-Fi input with its URL and token
    // file; a stream whose URL was not reported keeps the URL and takes the token file; a USB camera becomes the camera
    // input when camera, the system's entry for it, is given. Anything else leaves the input as it was.
    public static AppSettings Apply(AppSettings settings, ProvisionedInput input, CameraSettings? camera = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(input);
        return input switch
        {
            MjpegStreamReady(var address, var tokenFile) => settings with
            {
                Input = settings.Input with { Kind = InputSourceKind.Mjpeg, MjpegUrl = address.ToString(), TokenFilePath = tokenFile }
            },
            InputNotConfirmed { TokenFile: { } tokenFile } => settings with
            {
                Input = settings.Input with { Kind = InputSourceKind.Mjpeg, TokenFilePath = tokenFile }
            },
            UsbCameraReady(var name) when camera is not null && camera.Name == name => settings with
            {
                Input = settings.Input with { Kind = InputSourceKind.Camera, Camera = camera }
            },
            _ => settings,
        };
    }
}
