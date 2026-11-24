using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class TrackingPipelineTests
{
    private static readonly Rgb24FrameImage Red = new([255, 0, 0], 1, 1, 3);

    [Theory]
    [InlineData(-0.25f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.25f, 0.25f)]
    [InlineData(1.25f, 1f)]
    public void MouthEstimate_RawValue_KeepsRawAndClampsJawOpen(float raw, float expected)
    {
        // When
        var estimate = new MouthEstimate(raw);

        // Then
        Assert.Equal(raw, estimate.RawJawOpen);
        Assert.Equal(expected, estimate.JawOpen);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MouthEstimate(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MouthEstimate(float.PositiveInfinity));
    }

    [Fact]
    public async Task Run_SourceEnds_ProcessesLastFrameThenDisposesModel()
    {
        // Given
        var source = new FakeSource { Image = Red };
        var model = new FakeEstimator();
        var sink = new Sink(_ => Assert.False(model.Disposed));

        // When
        var exit = await Run(source, model, sink);

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.True(source.Released);
        Assert.True(model.Disposed);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(1, frame.Diagnostics.Processed);
        Assert.Equal(1f, frame.Result!.Estimate.JawOpen);
        Assert.Equal(1f, frame.Result!.Expressions[UnifiedExpression.JawOpen]);
        Assert.Equal(1, frame.Result!.Expressions.ProvidedCount);
        Assert.Same(Red, frame.Diagnostics.Source);
        Assert.Equal(new byte[] { 255, 0, 0 }, model.Pixels);
        Assert.Equal(new byte[] { 255, 0, 0 }, frame.Diagnostics.Pixels);
    }

    [Fact]
    public async Task Run_NoModel_StillDecodesAndPreprocesses()
    {
        // Given
        var sink = new Sink();

        // When
        var exit = await Run(new FakeSource { Image = Red }, null, sink);

        // Then
        Assert.True(exit is Ok<Unit>);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(TrackingPipeline.UnmodeledOutputSize, frame.Diagnostics.PixelWidth);
        Assert.Equal(TrackingPipeline.UnmodeledOutputSize, frame.Diagnostics.PixelHeight);
        Assert.Equal(frame.Diagnostics.PixelWidth * frame.Diagnostics.PixelHeight * 3, frame.Diagnostics.Pixels.Length);
        Assert.Equal(new byte[] { 255, 0, 0 }, frame.Diagnostics.Pixels[^3..]);
        Assert.Null(frame.Result);
        Assert.Null(frame.Diagnostics.InferenceMilliseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CalibrationOnOrOff_KeepsRawAndReportsLearningSeparately(bool enabled)
    {
        // Given
        var model = new FakeEstimator();
        var sink = new Sink();

        // When
        var exit = await Run(new FakeSource { Image = Red }, model, sink, options: new() { AdaptiveCalibration = enabled });

        // Then
        Assert.True(exit is Ok<Unit>);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(1, frame.Result!.Estimate.RawJawOpen);
        if (enabled)
        {
            Assert.False(frame.Result!.Calibration!.Value.IsReady);
            Assert.Null(frame.Result!.Calibration!.Value.JawOpen);
        }
        else Assert.Null(frame.Result!.Calibration);
        Assert.True(model.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_InferenceFailsOrDies_ReleasesSourceBeforeDisposingModel(bool defect)
    {
        // Given
        var source = new FakeSource { Image = Red, WaitForCancellation = true };
        var model = new FakeEstimator
        {
            Failure = "inference failed",
            Defect = defect,
            BeforeDispose = () => Assert.True(source.Released)
        };

        // When
        var exit = await Run(source, model, new Sink(_ => throw new InvalidOperationException("must not publish")));

        // Then
        if (defect) Assert.Equal("inference defect", Assert.IsType<Die>(exit.Value).Exception.Message);
        else Assert.True(exit is Err<TrackingError>(TrackingModelFailed(ModelInferenceFailed("inference failed"))));
        Assert.True(model.Disposed);
        Assert.False(model.DisposedWhileRunning);
    }

    [Fact]
    public async Task Run_SinkThrows_DiesAndReleasesBothResources()
    {
        // Given
        var source = new FakeSource { Image = Red, WaitForCancellation = true };
        var model = new FakeEstimator();
        var failure = new InvalidOperationException("output failed");

        // When
        var exit = await Run(source, model, new Sink(_ => throw failure));

        // Then
        Assert.Same(failure, Assert.IsType<Die>(exit.Value).Exception);
        Assert.True(source.Released);
        Assert.True(model.Disposed);
    }

    [Fact]
    public async Task Run_OutputFails_FailsWithOutputErrorAndReleasesBothResources()
    {
        // Given
        var source = new FakeSource { Image = Red, WaitForCancellation = true };
        var model = new FakeEstimator();

        // When
        var exit = await Run(source, model, new Sink { Failure = new("send failed", 10054) });

        // Then
        Assert.True(exit is Err<TrackingError>(TrackingOutputFailed(OutputFailed("send failed", 10054))));
        Assert.True(source.Released);
        Assert.True(model.Disposed);
    }

    [Fact]
    public void Publish_CompositeSink_PublishesInOrderAndStopsAtFirstFailure()
    {
        // Given
        var first = new Sink();
        var failing = new Sink { Failure = new("closed") };
        var last = new Sink();
        var frame = new TrackingFrame(null, new(Red, 0, new(PreprocessSettings.Default, 0), [255, 0, 0], 1, 1, 0, null, 0, 1, default));

        // When
        var result = new CompositeTrackingSink(first, failing, last).Publish(frame);

        // Then
        Assert.True(result is Err<OutputFailed>(OutputFailed("closed", null)));
        Assert.Single(first.Frames);
        Assert.Single(failing.Frames);
        Assert.Empty(last.Frames);
        Assert.True(new CompositeTrackingSink(first, last).Publish(frame) is Ok<Unit>);
    }

    [Fact]
    public async Task Run_CancelledDuringInference_WaitsForInferenceAndInputBeforeDisposal()
    {
        // Given
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new FakeSource { Image = Red, WaitForCancellation = true };
        var model = new FakeEstimator { BlockUntilCancelled = true };
        var run = Run(source, model, new Sink(), cancellationToken: cancellation.Token);
        await Task.WhenAll(model.Entered.Task, source.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // When
        cancellation.Cancel();

        // Then
        Assert.True(await run is Interrupted);
        Assert.True(source.Released);
        Assert.True(model.Disposed);
        Assert.False(model.DisposedWhileRunning);
    }

    [Fact]
    public async Task Run_UndecodableFramesInARow_FailsWithTheDecodeErrorAndReleasesSource()
    {
        // Given
        var source = new FakeSource { WaitForCancellation = true };
        var frames = new LatestFrameBuffer();
        var decoder = new EncodedFailsDecoder();
        var run = TrackingPipeline.Run(source, frames, decoder, null, new(), new Sink())
            .RunExitAsync(new(TimeProvider.System), TestContext.Current.CancellationToken).AsTask();

        // When
        for (var i = 1; i < TrackingPipeline.MaxUndecodableInARow; i++) await Undecodable(frames, decoder);
        Assert.False(run.IsCompleted);
        frames.Publish(Broken, 0);

        // Then
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(exit is Err<TrackingError>(TrackingInputFailed(InputDataInvalid("not an image"))));
        Assert.True(source.Released);
    }

    [Fact]
    public async Task Run_UndecodableFramesBetweenReadableOnes_SkipsAndCountsThem()
    {
        // Given
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frames = new LatestFrameBuffer();
        var decoder = new EncodedFailsDecoder();
        var sink = new Sink();
        var run = TrackingPipeline.Run(new FakeSource { WaitForCancellation = true }, frames, decoder, null, new(), sink)
            .RunExitAsync(new(TimeProvider.System), cancellation.Token).AsTask();

        // When: one short of the limit, a readable frame, then one short of the limit again.
        for (var i = 1; i < TrackingPipeline.MaxUndecodableInARow; i++) await Undecodable(frames, decoder);
        frames.Publish(Red, 0);
        await sink.WaitFor(frame => frame.Diagnostics.Processed == 1, cancellation.Token);
        for (var i = 1; i < TrackingPipeline.MaxUndecodableInARow; i++) await Undecodable(frames, decoder);
        frames.Publish(Red, 0);

        // Then
        var last = await sink.WaitFor(frame => frame.Diagnostics.Processed == 2, cancellation.Token);
        cancellation.Cancel();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
        Assert.Equal(2L * (TrackingPipeline.MaxUndecodableInARow - 1), last.Diagnostics.Undecodable);
    }

    private static readonly EncodedFrameImage Broken = new([1, 2, 3]);

    // Publishes a frame the decoder rejects and waits until the pipeline has tried it.
    private static async Task Undecodable(LatestFrameBuffer frames, EncodedFailsDecoder decoder)
    {
        var decoded = decoder.NextDecode();
        frames.Publish(Broken, 0);
        await decoded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Run_ModelLoadFails_DoesNotStartSource()
    {
        // Given
        var source = new FakeSource { Image = Red };
        var load = Effect<TrackingEnvironment>.Fail<ModelError>(new ModelFileMissing("missing"));

        // When
        var exit = await TrackingPipeline.Run(source, new(), new PassThroughDecoder(), load, new(), new Sink())
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // Then
        Assert.True(exit is Err<TrackingError>(TrackingModelFailed(ModelFileMissing("missing"))));
        Assert.False(source.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_InputFails_KeepsInputErrorAndReportsModelReleaseDefect(bool releaseFails)
    {
        // Given
        var source = new FakeSource { Failure = "input failed" };
        var model = new FakeEstimator
        {
            BeforeDispose = () =>
            {
                Assert.True(source.Released);
                if (releaseFails) throw new IOException("release failed");
            }
        };

        // When
        var exit = await Run(source, model, new Sink(_ => throw new InvalidOperationException("must not publish")));

        // Then
        if (releaseFails)
        {
            var die = Assert.IsType<Die>(exit.Value);
            Assert.Equal("release failed", die.Exception.Message);
            Assert.NotEmpty(die.Suppressed);
        }
        else Assert.True(exit is Err<TrackingError>(TrackingInputFailed(InputUnavailable("input failed", null))));
        Assert.True(model.Disposed);
    }

    [Fact]
    public async Task Update_SettingsChanged_ReprocessesHeldFrameWithNewSettings()
    {
        // Given
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var redBlue = new Rgb24FrameImage([255, 0, 0, 0, 0, 255], 2, 1, 6);
        var control = new TrackingControl(new(ResultAssert.Ok(CropRegion.Create(0, 0, .5, 1)), QuarterTurn.None, false));
        var sink = new Sink();
        var run = Run(new FakeSource { Image = redBlue, WaitForCancellation = true }, null, sink, control,
            cancellationToken: cancellation.Token);
        await sink.WaitFor(frame => frame.Diagnostics.Processed == 1, cancellation.Token);

        // When
        var revision = control.Update(new(ResultAssert.Ok(CropRegion.Create(.5, 0, .5, 1)), QuarterTurn.None, false));

        // Then
        var updated = await sink.WaitFor(frame => frame.Diagnostics.Settings.Revision == revision, cancellation.Token);
        cancellation.Cancel();
        Assert.True(await run is Interrupted);
        var first = sink.Frames.First();
        Assert.Equal(new byte[] { 255, 0, 0 }, first.Diagnostics.Pixels[..3]);
        Assert.Equal(new byte[] { 0, 0, 255 }, updated.Diagnostics.Pixels[..3]);
        Assert.Same(redBlue, updated.Diagnostics.Source);
        Assert.Equal(2, updated.Diagnostics.Processed);
        Assert.Equal(1, updated.Diagnostics.Statistics.Received);
    }

    [Fact]
    public async Task Run_FramesAndSettingsChangeWhileTimeStandsStill_ProcessesEachWithoutPolling()
    {
        // Given: a clock that never moves, so only the arrival or the change itself can wake the pipeline.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FakeTimeProvider();
        var frames = new LatestFrameBuffer();
        var control = new TrackingControl();
        var sink = new Sink();
        var run = TrackingPipeline.Run(new FakeSource { WaitForCancellation = true }, frames, new PassThroughDecoder(), null,
                control, sink)
            .RunExitAsync(new(clock), cancellation.Token).AsTask();

        // When
        frames.Publish(Red, clock.GetTimestamp());
        await sink.WaitFor(frame => frame.Diagnostics.Processed == 1, cancellation.Token);
        frames.Publish(new Rgb24FrameImage([0, 0, 255], 1, 1, 3), clock.GetTimestamp());
        await sink.WaitFor(frame => frame.Diagnostics.Processed == 2, cancellation.Token);
        var revision = control.ResetCalibration();

        // Then
        var reset = await sink.WaitFor(frame => frame.Diagnostics.Settings.Revision == revision, cancellation.Token);
        cancellation.Cancel();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
        Assert.Equal(3, reset.Diagnostics.Processed);
        Assert.Equal(new byte[] { 0, 0, 255 }, reset.Diagnostics.Pixels[..3]);
    }

    [Fact]
    public async Task ResetCalibration_AfterLearning_RestartsLearningAndReprocessesHeldFrame()
    {
        // Given
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FakeTimeProvider();
        var frames = new LatestFrameBuffer();
        var control = new TrackingControl();
        var sink = new Sink();
        var run = TrackingPipeline.Run(new FakeSource { WaitForCancellation = true }, frames, new PassThroughDecoder(),
                Load(new FakeEstimator()), control, sink, new ExpressionOptions { Smoothing = false, Deadzone = Deadzone.None })
            .RunExitAsync(new(clock), cancellation.Token).AsTask();
        for (var i = 0; i <= 60; i++)
        {
            if (i > 0) clock.Advance(TimeSpan.FromMilliseconds(100));
            var value = i % 20 < 10 ? (byte)26 : (byte)230;
            frames.Publish(new Rgb24FrameImage([value, value, value], 1, 1, 3), clock.GetTimestamp());
            await sink.WaitFor(frame => frame.Diagnostics.Processed == i + 1, cancellation.Token);
        }
        var learned = sink.Latest!;
        Assert.True(learned.Result!.Calibration!.Value.IsReady);
        // Once calibration is ready, the Unified Expressions JawOpen carries the calibrated value, not the raw one.
        Assert.Equal(learned.Result!.Calibration!.Value.JawOpen, learned.Result!.Expressions[UnifiedExpression.JawOpen]);
        Assert.NotEqual(learned.Result!.Estimate.JawOpen, learned.Result!.Expressions[UnifiedExpression.JawOpen]);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        // When
        var revision = control.ResetCalibration();

        // Then
        var reset = await sink.WaitFor(frame => frame.Diagnostics.Settings.Revision == revision, cancellation.Token);
        cancellation.Cancel();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) is Interrupted);
        Assert.False(reset.Result!.Calibration!.Value.IsReady);
        Assert.Null(reset.Result!.Calibration!.Value.JawOpen);
        Assert.Equal(reset.Result!.Estimate.JawOpen, reset.Result!.Expressions[UnifiedExpression.JawOpen]);
        Assert.Equal(62, reset.Diagnostics.Processed);
    }

    [Fact]
    public void Update_InvalidOrValidSettings_RejectsInvalidAndCountsEveryChange()
    {
        // When / Then: invalid settings are rejected.
        Assert.Throws<ArgumentNullException>(() => new PreprocessSettings(null!, QuarterTurn.None, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreprocessSettings(CropRegion.FullFrame, (QuarterTurn)4, false));

        // Given
        var control = new TrackingControl();
        Assert.Equal(PreprocessSettings.Default, control.Current.Preprocessing);
        Assert.Equal(0, control.Current.Revision);
        var mirrored = new PreprocessSettings(CropRegion.FullFrame, QuarterTurn.None, true);

        // When / Then: every change counts as a revision.
        Assert.Equal(1, control.Update(mirrored));
        Assert.Equal(2, control.ResetCalibration());
        Assert.Equal(new TrackingControlState(mirrored, 2), control.Current);
    }

    private static Task<Exit<Unit, TrackingError>> Run(FakeSource source, FakeEstimator? model, Sink sink,
        TrackingControl? control = null, ExpressionOptions? options = null, CancellationToken? cancellationToken = null) =>
        TrackingPipeline.Run(source, new(), new PassThroughDecoder(), model is null ? null : Load(model), control ?? new(), sink,
                options)
            // The test's own token stops a hung session when the run is cancelled.
            .RunExitAsync(new(TimeProvider.System), cancellationToken ?? TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static Effect<IMouthEstimator, ModelError, TrackingEnvironment> Load(FakeEstimator model) =>
        Effect<TrackingEnvironment>.Sync<IMouthEstimator>(() => model);


    // Passes RGB images through and rejects encoded ones, signalling each attempt.
    private sealed class EncodedFailsDecoder : IFrameDecoder
    {
        private volatile TaskCompletionSource? next;

        // Completes when the next frame has been tried; call it before publishing that frame.
        public Task NextDecode()
        {
            var attempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            next = attempt;
            return attempt.Task;
        }

        public Result<Rgb24FrameImage, InputDataInvalid> ToRgb24(FrameImage image)
        {
            Result<Rgb24FrameImage, InputDataInvalid> result = image is Rgb24FrameImage rgb
                ? new Ok<Rgb24FrameImage>(rgb) : new Err<InputDataInvalid>(new("not an image"));
            next?.TrySetResult();
            return result;
        }
    }

    private sealed class PassThroughDecoder : IFrameDecoder
    {
        public Result<Rgb24FrameImage, InputDataInvalid> ToRgb24(FrameImage image) => new Ok<Rgb24FrameImage>(Assert.IsType<Rgb24FrameImage>(image));
    }

    private sealed class Sink(Action<TrackingFrame>? onPublish = null) : ITrackingSink
    {
        private readonly ConcurrentQueue<TrackingFrame> frames = new();
        private TrackingFrame? latest;
        private TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutputFailed? Failure { get; init; }
        public IReadOnlyCollection<TrackingFrame> Frames => frames;
        public TrackingFrame? Latest => Volatile.Read(ref latest);

        public Result<Unit, OutputFailed> Publish(TrackingFrame frame)
        {
            onPublish?.Invoke(frame);
            frames.Enqueue(frame);
            Volatile.Write(ref latest, frame);
            Interlocked.Exchange(ref published, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return Failure is { } failure ? new Err<OutputFailed>(failure) : new Ok<Unit>(Unit.Value);
        }

        // Waits for a published frame that meets the condition, checking again after each publication.
        public async Task<TrackingFrame> WaitFor(Func<TrackingFrame, bool> condition, CancellationToken cancellationToken)
        {
            while (true)
            {
                var next = Volatile.Read(ref published).Task;
                if (Latest is { } frame && condition(frame)) return frame;
                await next.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class FakeEstimator : IMouthEstimator
    {
        public int InputWidth => 1;
        public int InputHeight => 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockUntilCancelled { get; init; }
        public string? Failure { get; init; }
        public bool Defect { get; init; }
        public Action? BeforeDispose { get; init; }
        public byte[]? Pixels { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedWhileRunning { get; private set; }
        private bool running;

        public Result<MouthEstimate, ModelInferenceFailed> Estimate(ReadOnlySpan<byte> pixels, CancellationToken ct)
        {
            Assert.False(Disposed);
            running = true;
            Entered.TrySetResult();
            try
            {
                if (BlockUntilCancelled) { ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); }
                if (Defect) throw new InvalidOperationException("inference defect");
                if (Failure is { } error) return new Err<ModelInferenceFailed>(new(error));
                Pixels = pixels.ToArray();
                return new Ok<MouthEstimate>(new(pixels[0] / 255f));
            }
            finally { running = false; }
        }

        public void Dispose()
        {
            DisposedWhileRunning = running;
            Disposed = true;
            BeforeDispose?.Invoke();
        }
    }

    private sealed class FakeSource : IFrameSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FrameImage? Image { get; init; }
        public bool WaitForCancellation { get; init; }
        public string? Failure { get; init; }
        public bool Released { get; private set; }

        public Effect<Unit, FrameSourceError, TrackingEnvironment> Run(IFrameWriter frames) =>
            Scope<FrameSourceError, TrackingEnvironment>.Gen(async scope =>
            {
                scope.Defer(_ => Effect<TrackingEnvironment>.Sync(() => { Released = true; return Unit.Value; }));
                Entered.TrySetResult();
                if (Failure is { } failure) throw scope.Fail(new InputUnavailable(failure));
                var clock = scope.Env.Resolve<TimeProvider>();
                if (Image is { } image) frames.Publish(image, clock.GetTimestamp());
                if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, clock, scope.CancellationToken);
                return Unit.Value;
            });
    }
}
