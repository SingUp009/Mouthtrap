using System.IO;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Sessions.Tests;

public sealed class ProvisionedSettingsTests
{
    private static readonly StreamUrl Camera = ResultAssert.Ok(StreamUrl.Create("http://192.168.1.20/stream"));
    private static readonly AppSettings Saved = new()
    {
        Input = new() { Kind = InputSourceKind.Video, VideoPath = @"C:\clips\mouth.mp4", MjpegUrl = "http://old/stream" },
    };

    [Fact]
    public void TokenFile_NoneChosen_IsNextToTheSettingsFile()
    {
        // Given
        var settings = Path.Combine(Path.GetTempPath(), "mouthtrap", "settings.json");

        // When / Then
        Assert.Equal(Path.Combine(Path.GetTempPath(), "mouthtrap", "stream-token.txt"), ProvisionedSettings.TokenFile(settings, null));
    }

    [Fact]
    public void TokenFile_Chosen_IsTheChosenOne()
    {
        // When / Then
        Assert.Equal(@"D:\keys\token.txt", ProvisionedSettings.TokenFile("settings.json", @"D:\keys\token.txt"));
    }

    [Fact]
    public void Apply_Stream_SelectsTheWifiInputWithItsUrlAndToken()
    {
        // When
        var settings = ProvisionedSettings.Apply(Saved, new MjpegStreamReady(Camera, @"C:\token.txt"));

        // Then
        Assert.Equal(new InputSettings
        {
            Kind = InputSourceKind.Mjpeg,
            VideoPath = @"C:\clips\mouth.mp4",
            MjpegUrl = "http://192.168.1.20/stream",
            TokenFilePath = @"C:\token.txt"
        }, settings.Input);
    }

    [Fact]
    public void Apply_StreamWithoutAReportedUrl_KeepsTheUrlAndTakesTheToken()
    {
        // When
        var settings = ProvisionedSettings.Apply(Saved, new InputNotConfirmed(InputNotConfirmedReason.NotReported, "No URL.", @"C:\token.txt"));

        // Then
        Assert.Equal((InputSourceKind.Mjpeg, "http://old/stream", @"C:\token.txt"), (settings.Input.Kind, settings.Input.MjpegUrl, settings.Input.TokenFilePath));
    }

    [Fact]
    public void Apply_UsbCameraTheSystemLists_SelectsTheCameraInput()
    {
        // Given
        var camera = new CameraSettings("UVC CAM1", @"\\?\usb#vid_303a&pid_8000");

        // When
        var settings = ProvisionedSettings.Apply(Saved, new UsbCameraReady("UVC CAM1"), camera);

        // Then
        Assert.Equal((InputSourceKind.Camera, camera), (settings.Input.Kind, settings.Input.Camera));
    }

    [Theory]
    [MemberData(nameof(Unchanged))]
    public void Apply_InputWithNothingToSelect_LeavesTheSettings(ProvisionedInput input, CameraSettings? camera)
    {
        // When / Then
        Assert.Same(Saved, ProvisionedSettings.Apply(Saved, input, camera));
    }

    public static TheoryData<ProvisionedInput, CameraSettings?> Unchanged => new()
    {
        { new UsbCameraReady("UVC CAM1"), null },
        { new UsbCameraReady("UVC CAM1"), new CameraSettings("Integrated Camera", "id") },
        { new InputNotConfirmed(InputNotConfirmedReason.RestartByHand, "Press RESET."), null },
    };
}
