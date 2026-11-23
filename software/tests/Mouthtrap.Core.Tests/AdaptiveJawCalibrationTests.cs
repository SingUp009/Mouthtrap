using Mouthtrap.Core;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class AdaptiveJawCalibrationTests
{
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.8f)]
    public void Observe_ConstantValue_NeverLearnsRange(float value)
    {
        // Given
        var calibration = new AdaptiveJawCalibration();

        // When
        var result = Feed(calibration, 0, 60, _ => value);

        // Then
        Assert.False(result.IsReady);
        Assert.Null(result.JawOpen);
    }

    [Fact]
    public void Observe_BothEndsSeen_LearnsRangeAndNormalizes()
    {
        // Given
        var calibration = Learned();

        // When
        var result = calibration.Observe(.5f, TimeSpan.FromSeconds(6));

        // Then
        Assert.True(result.IsReady);
        Assert.Equal(.1f, result.Lower);
        Assert.Equal(.9f, result.Upper);
        Assert.InRange(result.JawOpen!.Value, .499f, .501f);
    }

    [Theory]
    [InlineData(.9f, 1f)]
    [InlineData(.1f, 0f)]
    public void Observe_OneExpressionHeld_KeepsLearnedRange(float value, float expected)
    {
        // Given
        var calibration = Learned();

        // When
        var result = Feed(calibration, 6, 80, _ => value);

        // Then
        Assert.Equal(.1f, result.Lower);
        Assert.Equal(.9f, result.Upper);
        Assert.Equal(expected, result.JawOpen);
    }

    [Fact]
    public void Observe_IsolatedOutliers_NeitherLearnNorMoveRange()
    {
        // When / Then: outliers alone do not learn a range.
        var cold = new AdaptiveJawCalibration();
        Assert.False(Feed(cold, 0, 20, i => i == 25 ? 0 : i == 65 ? 1 : .5f).IsReady);

        // Given
        var calibration = Learned();

        // When
        var result = Feed(calibration, 6, 30, i => i == 81 ? 0 : i == 125 ? 1 : Ends(i));

        // Then
        Assert.InRange(result.Lower!.Value, .099f, .101f);
        Assert.InRange(result.Upper!.Value, .899f, .901f);
    }

    [Fact]
    public void Observe_NarrowerOpenAndClose_MovesBothEndsInwardWithinSlewLimit()
    {
        // Given
        var calibration = Learned();
        float? previousLower = .1f;
        float? previousUpper = .9f;
        CalibrationSnapshot result = default;

        // When / Then: every step stays within the slew limit.
        for (var i = 60; i <= 600; i++)
        {
            result = calibration.Observe(i % 20 < 10 ? .2f : .8f, TimeSpan.FromMilliseconds(i * 100));
            Assert.InRange(Math.Abs(result.Lower!.Value - previousLower!.Value), 0, .02001f);
            Assert.InRange(Math.Abs(result.Upper!.Value - previousUpper!.Value), 0, .02001f);
            previousLower = result.Lower;
            previousUpper = result.Upper;
        }

        // Then
        Assert.InRange(result.Lower!.Value, .199f, .201f);
        Assert.InRange(result.Upper!.Value, .799f, .801f);
    }

    [Fact]
    public void Observe_ContinuedDrift_StaysBoundedAroundLearnedRange()
    {
        // Given
        var calibration = Learned();

        // When
        var result = Feed(calibration, 6, 60, i => i % 20 < 10 ? .25f : .75f);
        result = Feed(calibration, 61, 150, i => i % 20 < 10 ? .35f : .65f);

        // Then
        Assert.InRange(result.Lower!.Value, .1f, .30001f);
        Assert.InRange(result.Upper!.Value, .69999f, .9f);
    }

    [Fact]
    public void Reset_AfterExpiredHistory_ForgetsOldEvidence()
    {
        // Given
        var calibration = Learned();
        var result = calibration.Observe(.9f, TimeSpan.FromSeconds(300));
        Assert.Equal(.1f, result.Lower);
        Assert.Equal(.9f, result.Upper);

        // When
        calibration.Reset();

        // Then
        Assert.False(Feed(calibration, 301, 320, _ => .9f).IsReady);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(120)]
    public void Observe_DifferentFrameRates_LearnsByElapsedTime(int fps)
    {
        // Given
        var calibration = new AdaptiveJawCalibration();
        CalibrationSnapshot result = default;

        // When
        for (var i = 0; i <= fps * 10; i++)
            result = calibration.Observe((i / fps) % 2 == 0 ? .1f : .9f, TimeSpan.FromSeconds((double)i / fps));

        // Then
        Assert.True(result.IsReady);
        Assert.Equal(.1f, result.Lower);
        Assert.Equal(.9f, result.Upper);
    }

    [Fact]
    public void Observe_FirstRange_UsesLinearlyInterpolatedQuantiles()
    {
        // Given
        var calibration = new AdaptiveJawCalibration();

        // When
        var result = Feed(calibration, 0, 2, i => Math.Min(i, 19) / 19f);

        // Then
        Assert.True(result.IsReady);
        Assert.InRange(result.Lower!.Value, 2 / 19f - .00001f, 2 / 19f + .00001f);
        Assert.InRange(result.Upper!.Value, 18 / 19f - .00001f, 18 / 19f + .00001f);
    }

    [Fact]
    public void Observe_InvalidValueOrTime_ThrowsAndClampsFiniteValues()
    {
        // Given
        var calibration = Learned();

        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => calibration.Observe(float.NaN, TimeSpan.FromSeconds(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => calibration.Observe(float.PositiveInfinity, TimeSpan.FromSeconds(6)));
        Assert.Equal(1f, calibration.Observe(2, TimeSpan.FromSeconds(6)).JawOpen);
        Assert.Throws<ArgumentOutOfRangeException>(() => calibration.Observe(.5f, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => calibration.Observe(.5f, TimeSpan.FromSeconds(-1)));
    }

    private static float Ends(int i) => i % 20 < 10 ? .1f : .9f;
    private static AdaptiveJawCalibration Learned()
    {
        var calibration = new AdaptiveJawCalibration();
        Assert.True(Feed(calibration, 0, 5.9, Ends).IsReady);
        return calibration;
    }
    private static CalibrationSnapshot Feed(AdaptiveJawCalibration calibration, double start, double end, Func<int, float> value)
    {
        CalibrationSnapshot result = default;
        for (var i = (int)Math.Round(start * 10); i <= (int)Math.Round(end * 10); i++)
            result = calibration.Observe(value(i), TimeSpan.FromMilliseconds(i * 100));
        return result;
    }
}
