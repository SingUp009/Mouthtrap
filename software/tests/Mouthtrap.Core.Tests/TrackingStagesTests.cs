using Microsoft.Extensions.Time.Testing;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class TrackingStagesTests
{
    private static readonly Rgb24FrameImage RedBlue = new([255, 0, 0, 0, 0, 255], 2, 1, 6);
    private static readonly ExpressionOptions Raw = new() { AdaptiveCalibration = false, Smoothing = false, Deadzone = Deadzone.None };

    [Fact]
    public void Process_NoModel_PreprocessesAtUnmodeledSizeOnly()
    {
        // Given
        var stages = new TrackingStages(null, ExpressionOptions.Default);

        // When
        var processed = ResultAssert.Ok(stages.Process(Select(RedBlue), 0, new FakeTimeProvider(),
            TestContext.Current.CancellationToken));

        // Then
        Assert.Equal(TrackingStages.UnmodeledOutputSize, processed.PixelWidth);
        Assert.Equal(TrackingStages.UnmodeledOutputSize, processed.PixelHeight);
        Assert.Equal(processed.PixelWidth * processed.PixelHeight * 3, processed.Pixels.Length);
        Assert.Null(processed.Estimate);
        Assert.Null(processed.InferenceMilliseconds);
        Assert.Null(processed.Calibration);
        Assert.Null(processed.Expressions);
    }

    [Fact]
    public void Process_WithModel_EstimatesPreprocessedPixelsAndExpressesThem()
    {
        // Given
        var model = new Estimator();
        var stages = new TrackingStages(model, Raw);
        var crop = new PreprocessSettings(ResultAssert.Ok(CropRegion.Create(.5, 0, .5, 1)), QuarterTurn.None, false);

        // When
        var processed = ResultAssert.Ok(stages.Process(Select(RedBlue, crop), 0, new FakeTimeProvider(),
            TestContext.Current.CancellationToken));

        // Then
        Assert.Equal(new byte[] { 0, 0, 255 }, model.Pixels);
        Assert.Equal(new byte[] { 0, 0, 255 }, processed.Pixels);
        Assert.Equal(0f, processed.Estimate!.JawOpen);
        Assert.Equal(0f, processed.Expressions![UnifiedExpression.JawOpen]);
        Assert.NotNull(processed.InferenceMilliseconds);
    }

    [Fact]
    public void Process_InferenceFails_ReturnsTheModelError()
    {
        // Given
        var stages = new TrackingStages(new Estimator { Failure = "inference failed" }, Raw);

        // When
        var result = stages.Process(Select(RedBlue), 0, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(result is Err<ModelInferenceFailed>(ModelInferenceFailed("inference failed")));
    }

    [Fact]
    public void Process_CancelledDuringInference_ThrowsCancellation()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var stages = new TrackingStages(new Estimator { OnEstimate = cancellation.Cancel }, Raw);

        // When / Then
        Assert.Throws<OperationCanceledException>(() =>
            stages.Process(Select(RedBlue), 0, new FakeTimeProvider(), cancellation.Token));
    }

    [Fact]
    public void Process_SettingsChanged_RestartsCalibration()
    {
        // Given
        var clock = new FakeTimeProvider();
        var startedAt = clock.GetTimestamp();
        var model = new Estimator();
        var stages = new TrackingStages(model, new ExpressionOptions { Smoothing = false, Deadzone = Deadzone.None });
        CalibrationSnapshot? learned = null;
        for (var i = 0; i <= 60; i++)
        {
            if (i > 0) clock.Advance(TimeSpan.FromMilliseconds(100));
            var value = i % 20 < 10 ? (byte)26 : (byte)230;
            var frame = new FrameSelection(new([value, value, value], 1, 1, 3), 0, new(PreprocessSettings.Default, 0), i == 0);
            learned = ResultAssert.Ok(stages.Process(frame, startedAt, clock, TestContext.Current.CancellationToken)).Calibration;
        }
        Assert.True(learned!.Value.IsReady);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        // When
        var reset = ResultAssert.Ok(stages.Process(new(new([26, 26, 26], 1, 1, 3), 0, new(PreprocessSettings.Default, 1), true),
            startedAt, clock, TestContext.Current.CancellationToken));

        // Then
        Assert.False(reset.Calibration!.Value.IsReady);
    }

    private static FrameSelection Select(Rgb24FrameImage image, PreprocessSettings? settings = null) =>
        new(image, 0, new(settings ?? PreprocessSettings.Default, 0), SettingsChanged: true);

    private sealed class Estimator : IMouthEstimator
    {
        public int InputWidth => 1;
        public int InputHeight => 1;
        public string? Failure { get; init; }
        public Action? OnEstimate { get; init; }
        public byte[]? Pixels { get; private set; }

        public Result<MouthEstimate, ModelInferenceFailed> Estimate(ReadOnlySpan<byte> rgb24, CancellationToken cancellationToken)
        {
            OnEstimate?.Invoke();
            Pixels = rgb24.ToArray();
            return Failure is { } failure ? new Err<ModelInferenceFailed>(new(failure)) : new Ok<MouthEstimate>(new(rgb24[0] / 255f));
        }

        public void Dispose() { }
    }
}
