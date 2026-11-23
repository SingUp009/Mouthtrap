using System.Runtime.InteropServices;
using FlashCap;
using FlashCap.FrameProcessors;
using FlashCap.Utilities;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Diagnostics;
using Mouthtrap.Inputs;
using Xunit;

namespace Mouthtrap.Inputs.Camera.Tests;

public sealed class CameraDiagnosticsTests
{
    [Fact]
    public async Task Enumerate_LazyDescriptors_EnumeratesOnRunWithoutOpeningCamera()
    {
        // Given
        var camera = new TestDescriptor("C200", new VideoCharacteristics(PixelFormats.JPEG, 1280, 720, 30));
        var calls = 0;
        var environment = new CameraEnvironment(new TestDevices(() =>
        {
            calls++;
            return [camera];
        }));
        var effect = CameraDiagnostics.Enumerate<CameraEnvironment>();
        Assert.Equal(0, calls);

        // When
        var result = await effect.RunExitAsync(environment, CancellationToken.None);

        // Then
        var devices = Assert.IsType<Ok<CaptureDeviceDescriptor[]>>(result.Value).Value;
        Assert.Same(camera, Assert.Single(devices));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Enumerate_NoDevices_SucceedsWithEmptyResult()
    {
        // When
        var result = await CameraDiagnostics.Enumerate<CameraEnvironment>()
            .RunExitAsync(new(new TestDevices(() => [])), CancellationToken.None);

        // Then
        Assert.Empty(Assert.IsType<Ok<CaptureDeviceDescriptor[]>>(result.Value).Value);
    }

    [Fact]
    public async Task Enumerate_ComFailureDuringIteration_FailsWithTypedError()
    {
        // Given
        static IEnumerable<CaptureDeviceDescriptor> Broken()
        {
            yield return new TestDescriptor("first");
            throw new COMException("access denied", unchecked((int)0x80070005));
        }

        // When
        var result = await CameraDiagnostics.Enumerate<CameraEnvironment>()
            .RunExitAsync(new(new TestDevices(Broken)), CancellationToken.None);

        // Then
        Assert.True(result is Err<CameraDiagnosticError>(CameraEnumerationFailed(unchecked((int)0x80070005), "access denied")));
    }

    [Fact]
    public async Task Enumerate_UnexpectedException_RemainsDefect()
    {
        // Given
        var defect = new InvalidOperationException("bug");

        // When
        var result = await CameraDiagnostics.Enumerate<CameraEnvironment>()
            .RunExitAsync(new(new TestDevices(() => throw defect)), CancellationToken.None);

        // Then
        Assert.Same(defect, Assert.IsType<Die>(result.Value).Exception);
    }

    [Fact]
    public async Task Enumerate_AlreadyCancelled_DoesNotEnumerate()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // When
        var result = await CameraDiagnostics.Enumerate<CameraEnvironment>()
            .RunExitAsync(new(new TestDevices(() => throw new InvalidOperationException("must not enumerate"))), cancellation.Token);

        // Then
        Assert.True(result is Interrupted);
    }

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

    private sealed class TestDevices(Func<IEnumerable<CaptureDeviceDescriptor>> enumerate) : CaptureDevices
    {
        protected override IEnumerable<CaptureDeviceDescriptor> OnEnumerateDescriptors() => enumerate();
    }

    private sealed class TestDescriptor(string name, params VideoCharacteristics[] modes)
        : CaptureDeviceDescriptor(name, "test camera", modes, new DefaultBufferPool())
    {
        public override object Identity => "fake-camera";
        public override DeviceTypes DeviceType => DeviceTypes.DirectShow;

        protected override Task<CaptureDevice> OnOpenWithFrameProcessorAsync(
            VideoCharacteristics characteristics, TranscodeFormats transcodeFormat,
            FrameProcessor frameProcessor, CancellationToken ct) =>
            throw new InvalidOperationException("Enumeration must not open the camera.");
    }
}
