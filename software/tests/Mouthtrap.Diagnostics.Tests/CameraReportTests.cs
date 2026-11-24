using FlashCap;
using FlashCap.FrameProcessors;
using FlashCap.Utilities;
using Mouthtrap.Diagnostics;
using Xunit;

namespace Mouthtrap.Diagnostics.Tests;

public sealed class CameraReportTests
{
    [Theory]
    [InlineData(PixelFormats.JPEG, 1280, 720, 30, 1, true)]
    [InlineData(PixelFormats.YUYV, 1280, 720, 30000, 1001, true)]
    [InlineData(PixelFormats.JPEG, 1280, 720, 10000000, 333333, true)]
    [InlineData(PixelFormats.Unknown, 1280, 720, 30, 1, false)]
    [InlineData(PixelFormats.JPEG, 1920, 1080, 30, 1, false)]
    [InlineData(PixelFormats.JPEG, 1280, 720, 25, 1, false)]
    [InlineData(PixelFormats.JPEG, 1280, 720, 0, 0, false)]
    public void IsPreviewCandidate_Mode_AcceptsNear30FpsModesAndRejectsUnsupported(
        PixelFormats pixelFormat, int width, int height, int numerator, int denominator, bool expected)
    {
        // Given
        var mode = new VideoCharacteristics(pixelFormat, width, height, new Fraction(numerator, denominator));

        // When
        var candidate = CameraReport.IsPreviewCandidate(mode);

        // Then
        Assert.Equal(expected, candidate);
    }

    [Fact]
    public void IsPreviewCandidate_NonDiscreteMode_ReturnsFalse()
    {
        // Given
        var mode = new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, 30, "range", false, "MJPG");

        // When
        var candidate = CameraReport.IsPreviewCandidate(mode);

        // Then
        Assert.False(candidate);
    }

    [Fact]
    public void Write_Devices_IncludesIdentityRawFormatRationalFpsAndUnsupportedModes()
    {
        // Given
        var supported = new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, new Fraction(30000, 1001));
        var unsupported = new VideoCharacteristics(PixelFormats.Unknown, 1280, 720, 30);
        using var output = new StringWriter();

        // When
        var exitCode = CameraReport.Write(output, [new TestDescriptor("C200", supported, unsupported)]);

        // Then
        Assert.Equal(0, exitCode);
        Assert.Contains("Name: C200", output.ToString());
        Assert.Contains("Identity: fake-camera", output.ToString());
        Assert.Contains("Backend: DirectShow", output.ToString());
        Assert.Contains("30000/1001", output.ToString());
        Assert.Contains("raw=Unknown", output.ToString());
        Assert.Contains("supported=False", output.ToString());
        Assert.Contains("720p/~30fps candidates: 1", output.ToString());
    }

    [Fact]
    public void Write_NoDevices_ReportsEmptyInventoryWithExitCodeTwo()
    {
        // Given
        using var output = new StringWriter();

        // When
        var exitCode = CameraReport.Write(output, []);

        // Then
        Assert.Equal(2, exitCode);
        Assert.Contains("No capture devices found", output.ToString());
    }

    private sealed class TestDescriptor(string name, params VideoCharacteristics[] modes)
        : CaptureDeviceDescriptor(name, "test camera", modes, new DefaultBufferPool())
    {
        public override object Identity => "fake-camera";
        public override DeviceTypes DeviceType => DeviceTypes.DirectShow;

        protected override Task<CaptureDevice> OnOpenWithFrameProcessorAsync(
            VideoCharacteristics characteristics, TranscodeFormats transcodeFormat,
            FrameProcessor frameProcessor, CancellationToken ct) =>
            throw new InvalidOperationException("A report must not open the camera.");
    }
}
