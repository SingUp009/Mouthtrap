using System.Runtime.InteropServices;
using FlashCap;
using FlashCap.FrameProcessors;
using FlashCap.Utilities;
using Moderato.Effects;
using Moderato.Results;
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
