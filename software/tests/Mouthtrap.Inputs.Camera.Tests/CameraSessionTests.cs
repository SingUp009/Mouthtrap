using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using FlashCap;
using FlashCap.FrameProcessors;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Inputs.Camera.Tests;

public sealed class CameraSessionTests
{
    [Theory]
    [InlineData("en", "Could not initialize the camera: busy")]
    [InlineData("ja", "カメラの初期化に失敗しました: busy")]
    public async Task Run_OpenFails_FailsAsUnavailableWithCodeInUiLanguage(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            // Given
            var descriptor = new FakeDescriptor(new FakeCapture()) { OpenFailure = new COMException("busy", unchecked((int)0x80070020)) };
            IFrameSource source = new CameraFrameSource(descriptor, descriptor.Characteristics[0]);

            // When
            var exit = await source.Run(new()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

            // Then
            Assert.True(exit is Err<FrameSourceError>(InputUnavailable(var message, unchecked((int)0x80070020))) && message == expected);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void CameraMessages_EveryMessage_HasJapaneseTranslation()
    {
        // Given
        var resources = new ResourceManager("Mouthtrap.Inputs.CameraMessages", typeof(CameraFrameSource).Assembly);

        // When / Then
        ResourceAssert.Translated(resources, "ja");
    }

    [Fact]
    public async Task Run_ModeUnavailable_FailsWithInvalidSettings()
    {
        // Given
        var device = new FakeCapture();
        IFrameSource source = new CameraFrameSource(new FakeDescriptor(device), new(PixelFormats.Unknown, 1280, 720, 30));

        // When
        var exit = await source.Run(new()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputSettingsInvalid));
        Assert.Empty(device.Events);
    }

    [Fact]
    public async Task Run_NoFramesForFiveSeconds_FailsWithLostInput()
    {
        // Given
        var descriptor = new FakeDescriptor(new FakeCapture());
        var clock = new TimerAwareTimeProvider();
        IFrameSource source = new CameraFrameSource(descriptor, descriptor.Characteristics[0]);
        var run = source.Run(new()).RunExitAsync(new(clock), CancellationToken.None).AsTask();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        clock.Advance(TimeSpan.FromSeconds(5));

        // Then
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) is Err<FrameSourceError>(InputLost));
    }

    [Fact]
    public async Task Run_Cancelled_StopsAndDisposesWithUncancelledCleanupToken()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var device = new FakeCapture();
        var clock = new TimerAwareTimeProvider();
        var descriptor = new FakeDescriptor(device);
        var run = CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(clock), cancellation.Token).AsTask();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["open", "start"], device.Events);

        // When
        cancellation.Cancel();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Interrupted);
        Assert.Equal(["open", "start", "stop", "dispose"], device.Events);
        Assert.False(device.StopWasCancelled);
    }

    [Fact]
    public async Task Run_StartFails_FailsWithTypedErrorAndReleasesDevice()
    {
        // Given
        var device = new FakeCapture { StartFailure = new COMException("busy") };
        var descriptor = new FakeDescriptor(device);

        // When
        var exit = await CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<CameraSessionError>(CameraOperationFailed(CameraOperation.Start, _, "busy")));
        Assert.Equal(["open", "start", "stop", "dispose"], device.Events);
    }

    [Fact]
    public async Task Run_OpenFails_DoesNotReleaseUnacquiredDevice()
    {
        // Given
        var device = new FakeCapture();
        var descriptor = new FakeDescriptor(device) { OpenFailure = new COMException("denied") };

        // When
        var exit = await CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<CameraSessionError>(CameraOperationFailed(CameraOperation.Initialize, _, "denied")));
        Assert.Equal(["open"], device.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_StopFails_StillDisposesAndKeepsBothCleanupFailures(bool alsoFailDispose)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var stopFailure = new IOException("stop failed");
        var disposeFailure = new IOException("dispose failed");
        var device = new FakeCapture { StopFailure = stopFailure, DisposeFailure = alsoFailDispose ? disposeFailure : null };
        var descriptor = new FakeDescriptor(device);
        var clock = new TimerAwareTimeProvider();
        var run = CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(clock), cancellation.Token).AsTask();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        cancellation.Cancel();

        // Then
        var die = Assert.IsType<Die>((await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Value);
        Assert.Equal(["open", "start", "stop", "dispose"], device.Events);
        if (alsoFailDispose)
            Assert.Equal([stopFailure, disposeFailure], Assert.IsType<AggregateException>(die.Exception).InnerExceptions);
        else
            Assert.Same(stopFailure, die.Exception);
    }

    [Fact]
    public async Task Run_CancelledRightAfterOpen_ReleasesDevice()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var device = new FakeCapture();
        var descriptor = new FakeDescriptor(device) { AfterOpen = cancellation.Cancel };

        // When
        var exit = await CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(TimeProvider.System), cancellation.Token);

        // Then
        Assert.True(exit is Interrupted);
        Assert.Equal(["open", "stop", "dispose"], device.Events);
    }

    [Fact]
    public async Task Run_UnsupportedMode_FailsBeforeOpen()
    {
        // Given
        var device = new FakeCapture();
        var descriptor = new FakeDescriptor(device);

        // When
        var exit = await CameraSession.Run<PreviewEnvironment>(descriptor,
            new(PixelFormats.Unknown, 1280, 720, 30), new())
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<CameraSessionError>(InvalidCameraMode));
        Assert.Empty(device.Events);
    }

    [Fact]
    public async Task Run_SessionGetsNoFramesForFiveSeconds_FailsWithTypedErrorAndReleases()
    {
        // Given
        var device = new FakeCapture();
        var descriptor = new FakeDescriptor(device);
        var clock = new TimerAwareTimeProvider();
        var run = CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], new())
            .RunExitAsync(new(clock), CancellationToken.None).AsTask();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        clock.Advance(TimeSpan.FromSeconds(5));

        // Then
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(exit is Err<CameraSessionError>(CameraFramesTimedOut));
        Assert.Equal(["open", "start", "stop", "dispose"], device.Events);
    }

    [Fact]
    public async Task Run_FrameCallbackOrDecodeFails_DiesAndReleases()
    {
        // Given
        var device = new FakeCapture();
        var descriptor = new FakeDescriptor(device);
        var clock = new TimerAwareTimeProvider();
        var frames = new LatestFrameBuffer();
        var failure = new InvalidDataException("invalid image");
        var run = CameraSession.Run<PreviewEnvironment>(descriptor, descriptor.Characteristics[0], frames)
            .RunExitAsync(new(clock), CancellationToken.None).AsTask();
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // When
        frames.Fail(failure);
        clock.Advance(TimeSpan.FromSeconds(1));

        // Then
        var die = Assert.IsType<Die>((await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Value);
        Assert.Same(failure, die.Exception);
        Assert.Equal(["open", "start", "stop", "dispose"], device.Events);
    }

    [Fact]
    public void Take_SeveralPublishedFrames_ReturnsLatestWithTimestampAndCounts()
    {
        // Given
        var frames = new LatestFrameBuffer();
        frames.Publish([1], 0);
        frames.Publish([2], 33);

        // When
        var frame = Assert.IsType<PreviewFrame>(frames.Take());

        // Then
        Assert.Equal([2], Assert.IsType<EncodedFrameImage>(frame.Image).Data);
        Assert.Equal(33, frame.ReceivedAt);
        Assert.Equal(new FrameStatistics(2, 1, 33), frames.Statistics);
        Assert.Null(frames.Take());
        frames.Publish([3], 66);
        Assert.Equal(1, frames.Statistics.Replaced);
    }

    [Fact]
    public void Publish_ConcurrentPublishers_KeepsOneFrameAndCountsReplacements()
    {
        // Given
        var frames = new LatestFrameBuffer();

        // When
        Parallel.For(0, 1000, _ => frames.Publish([1], 0));

        // Then
        Assert.Equal(1000, frames.Statistics.Received);
        Assert.Equal(999, frames.Statistics.Replaced);
        Assert.NotNull(frames.Take());
        Assert.Null(frames.Take());
    }

    private sealed class FakeDescriptor(FakeCapture capture)
        : CaptureDeviceDescriptor("test", "test", [new(PixelFormats.JPEG, 1280, 720, 30)], new DefaultBufferPool())
    {
        public Exception? OpenFailure { get; init; }
        public Action? AfterOpen { get; init; }
        public override object Identity => "test";
        public override DeviceTypes DeviceType => DeviceTypes.DirectShow;

        protected override Task<CaptureDevice> OnOpenWithFrameProcessorAsync(VideoCharacteristics characteristics,
            TranscodeFormats transcodeFormat, FrameProcessor frameProcessor, CancellationToken ct)
        {
            capture.Events.Add("open");
            if (OpenFailure is { } failure) throw failure;
            AfterOpen?.Invoke();
            return Task.FromResult<CaptureDevice>(capture);
        }
    }

    private sealed class FakeCapture() : CaptureDevice("test", "test")
    {
        public List<string> Events { get; } = [];
        public Exception? StartFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public bool StopWasCancelled { get; private set; }

        protected override Task OnStartAsync(CancellationToken ct)
        {
            Events.Add("start");
            if (StartFailure is { } failure) throw failure;
            return Task.CompletedTask;
        }

        protected override Task OnStopAsync(CancellationToken ct)
        {
            Events.Add("stop");
            StopWasCancelled = ct.IsCancellationRequested;
            if (StopFailure is { } failure) throw failure;
            return Task.CompletedTask;
        }

        protected override Task OnDisposeAsync()
        {
            Events.Add("dispose");
            if (DisposeFailure is { } failure) throw failure;
            return Task.CompletedTask;
        }

        protected override Task OnInitializeAsync(VideoCharacteristics characteristics,
            TranscodeFormats transcodeFormat, FrameProcessor frameProcessor, CancellationToken ct) => Task.CompletedTask;
        protected override void OnCapture(IntPtr pData, int size, long timestampMicroseconds, long frameIndex, PixelBuffer buffer) =>
            throw new InvalidOperationException("No native capture in unit tests.");
    }
}
