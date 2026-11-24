using FlashCap;
using FlashCap.Utilities;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Xunit;

namespace Mouthtrap.Sessions.Tests;

public sealed class SavedCameraTests
{
    private static readonly CameraModeSettings Saved = new(1280, 720, "JPEG", 30);

    [Fact]
    public void IndexOf_SavedIdentityConnected_FindsItEvenWhenAnotherHasTheName()
    {
        // Given
        CaptureDeviceDescriptor[] devices = [new Camera("Desk camera", "usb#2"), new Camera("Desk camera", "usb#1")];

        // When / Then
        Assert.Equal(1, SavedCamera.IndexOf(devices, new("Desk camera", "usb#1")));
    }

    [Fact]
    public void IndexOf_CameraMovedToAnotherPort_FindsItByName()
    {
        // Given
        CaptureDeviceDescriptor[] devices = [new Camera("Other", "usb#1"), new Camera("Desk camera", "usb#3")];

        // When / Then
        Assert.Equal(1, SavedCamera.IndexOf(devices, new("Desk camera", "usb#2")));
    }

    [Fact]
    public void IndexOf_SavedCameraNotConnected_IsMinusOne()
    {
        // When / Then
        Assert.Equal(-1, SavedCamera.IndexOf([new Camera("Other", "usb#1")], new("Desk camera", "usb#2")));
    }

    [Theory]
    [InlineData(1280, 720, 30, 1)]
    [InlineData(1280, 720, 2997, 100)]
    public void Matches_SameSizeFormatAndCloseRate_IsTrue(int width, int height, int numerator, int denominator)
    {
        // Given
        var mode = new VideoCharacteristics(PixelFormats.JPEG, width, height, new Fraction(numerator, denominator));

        // When / Then
        Assert.True(SavedCamera.Matches(mode, Saved));
    }

    [Theory]
    [InlineData(PixelFormats.JPEG, 1920, 1080, 30)]
    [InlineData(PixelFormats.YUYV, 1280, 720, 30)]
    [InlineData(PixelFormats.JPEG, 1280, 720, 15)]
    public void Matches_DifferentSizeFormatOrRate_IsFalse(PixelFormats format, int width, int height, int fps)
    {
        // Given
        var mode = new VideoCharacteristics(format, width, height, fps);

        // When / Then
        Assert.False(SavedCamera.Matches(mode, Saved));
    }

    [Fact]
    public void ModeOf_CameraMovedToAnotherPort_FindsTheSavedMode()
    {
        // Given
        var saved = new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, 30);
        var camera = new Camera("Desk camera", "usb#3", new VideoCharacteristics(PixelFormats.JPEG, 640, 480, 30), saved);

        // When / Then
        Assert.Same(saved, SavedCamera.ModeOf(camera, new("Desk camera", "usb#2", Saved)));
    }

    [Fact]
    public void ModeOf_AnotherCameraWithTheSavedMode_IsNull()
    {
        // Given
        var camera = new Camera("Other", "usb#1", new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, 30));

        // When / Then
        Assert.Null(SavedCamera.ModeOf(camera, new("Desk camera", "usb#2", Saved)));
    }

    [Fact]
    public void ModeOf_SavedModeNotOffered_IsNull()
    {
        // Given
        var camera = new Camera("Desk camera", "usb#2", new VideoCharacteristics(PixelFormats.JPEG, 640, 480, 30));

        // When / Then
        Assert.Null(SavedCamera.ModeOf(camera, new("Desk camera", "usb#2", Saved)));
    }

    [Fact]
    public void ModeOf_NoModeSaved_IsNull()
    {
        // Given
        var camera = new Camera("Desk camera", "usb#2", new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, 30));

        // When / Then
        Assert.Null(SavedCamera.ModeOf(camera, new("Desk camera", "usb#2")));
    }

    private sealed class Camera(string name, string identity, params VideoCharacteristics[] modes)
        : CaptureDeviceDescriptor(name, "test camera", modes, new DefaultBufferPool())
    {
        public override object Identity => identity;
        public override DeviceTypes DeviceType => DeviceTypes.DirectShow;

        protected override Task<CaptureDevice> OnOpenWithFrameProcessorAsync(VideoCharacteristics characteristics,
            TranscodeFormats transcodeFormat, FrameProcessor frameProcessor, CancellationToken ct) =>
            throw new InvalidOperationException("Finding a camera must not open it.");
    }
}
