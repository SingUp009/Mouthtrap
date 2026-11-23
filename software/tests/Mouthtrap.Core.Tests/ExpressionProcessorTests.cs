using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;
using static Mouthtrap.Core.UnifiedExpression;

namespace Mouthtrap.Core.Tests;

public sealed class ExpressionProcessorTests
{
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    [Fact]
    public void Observe_FirstThreeSecondsThenMore_PassesClampedValuesThenMapsAboveRestingLevel()
    {
        // Given
        var baseline = new AdaptiveBaseline();
        for (var i = 0; i < 29; i++) Assert.Equal(.4f, baseline.Observe(.4f, Seconds(i * .1)));
        Assert.Null(baseline.Level);
        Assert.Equal(1f, baseline.Observe(1.3f, Seconds(2.9)));

        // When
        baseline.Observe(.4f, Seconds(3.0));

        // Then
        Assert.Equal(.4f, baseline.Level!.Value, 3);
        Assert.Equal(.5f, baseline.Observe(.7f, Seconds(3.05)), 3);
        Assert.Equal(0f, baseline.Observe(.2f, Seconds(3.06)));
    }

    [Fact]
    public void Observe_BriefExpressions_KeepsRestingLevel()
    {
        // Given
        var baseline = new AdaptiveBaseline();

        // When
        for (var i = 0; i <= 200; i++) baseline.Observe(i % 10 < 8 ? .3f : .9f, Seconds(i * .1));

        // Then
        Assert.Equal(.3f, baseline.Level!.Value, 3);
    }

    [Fact]
    public void Observe_RestingLevelDrifts_FollowsWithinThirtySeconds()
    {
        // Given
        var baseline = new AdaptiveBaseline();
        var t = 0.0;
        for (; t < 40; t += .1) baseline.Observe(.5f, Seconds(t));
        Assert.Equal(.5f, baseline.Level!.Value, 3);

        // When
        for (var end = t + 31; t < end; t += .1) baseline.Observe(.2f, Seconds(t));

        // Then
        Assert.Equal(.2f, baseline.Level!.Value, 3);
    }

    [Fact]
    public void Observe_HighRestingValues_CapsLevelSoScaleStaysFinite()
    {
        // Given
        var baseline = new AdaptiveBaseline();

        // When
        for (var i = 0; i <= 40; i++) baseline.Observe(1.5f, Seconds(i * .1));

        // Then
        Assert.Equal(.9f, baseline.Level!.Value, 3);
        Assert.Equal(1f, baseline.Observe(1.5f, Seconds(4.1)));
    }

    [Fact]
    public void Observe_NonFiniteValueOrEarlierTime_Throws()
    {
        // Given
        var baseline = new AdaptiveBaseline();
        baseline.Observe(.1f, Seconds(1));

        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => baseline.Observe(float.NaN, Seconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseline.Observe(.1f, Seconds(.5)));
    }

    [Fact]
    public void Filter_FirstSteadyAndStepInput_PassesFirstHoldsSteadyAndLagsLessWhenFaster()
    {
        // Given: the first value passes and steady input holds.
        var slow = new OneEuroFilter(.7f, 0f);
        var fast = new OneEuroFilter(.7f, 3f);
        Assert.Equal(.2f, slow.Filter(.2f, Seconds(0)));
        fast.Filter(.2f, Seconds(0));
        Assert.Equal(.2f, slow.Filter(.2f, Seconds(.066)), 5);
        fast.Filter(.2f, Seconds(.066));

        // When
        var slowStep = slow.Filter(1f, Seconds(.133));
        var fastStep = fast.Filter(1f, Seconds(.133));

        // Then
        Assert.InRange(slowStep, .21f, .99f);
        Assert.True(fastStep > slowStep, $"{fastStep} <= {slowStep}");
        Assert.Throws<ArgumentOutOfRangeException>(() => slow.Filter(float.PositiveInfinity, Seconds(.2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => slow.Filter(.5f, Seconds(.1)));
    }

    [Fact]
    public void Filter_SameTimeAgain_ReturnsLastValue()
    {
        // Given
        var filter = new OneEuroFilter(.7f, 3f);
        filter.Filter(.2f, Seconds(1));
        var next = filter.Filter(.8f, Seconds(1.1));

        // When
        var repeated = filter.Filter(.1f, Seconds(1.1));

        // Then
        Assert.Equal(next, repeated);
    }

    [Fact]
    public void Process_TongueOutputs_UnprovidedByDefaultAndSentWhenAsked()
    {
        // Given
        var estimate = new MouthEstimate(.5f, [new("TongueOut", .9f), new("MouthPucker", .6f)]);
        var plain = new ExpressionOptions { AdaptiveCalibration = false, Smoothing = false, Deadzone = 0 };

        // When
        var withoutTongue = new ExpressionProcessor(plain).Process(estimate, Seconds(0)).Expressions;
        var withTongue = new ExpressionProcessor(plain with { Tongue = true }).Process(estimate, Seconds(0)).Expressions;

        // Then
        Assert.Null(withoutTongue[TongueOut]);
        Assert.Equal(.6f, withoutTongue[LipPuckerLowerLeft]);
        Assert.Equal(.9f, withTongue[TongueOut]);
        Assert.Equal(.5f, withTongue[JawOpen]);
    }

    [Fact]
    public void Process_AllBabbleOutputsByDefault_Fills43Keys()
    {
        // Given
        string[] names =
        [
            "CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight", "JawForward", "JawLeft", "JawRight",
            "NoseSneerLeft", "NoseSneerRight", "MouthFunnel", "MouthPucker", "MouthLeft", "MouthRight", "MouthRollUpper",
            "MouthRollLower", "MouthShrugUpper", "MouthShrugLower", "MouthClose", "MouthSmileLeft", "MouthSmileRight",
            "MouthFrownLeft", "MouthFrownRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthUpperUpLeft", "MouthUpperUpRight",
            "MouthLowerDownLeft", "MouthLowerDownRight", "MouthPressLeft", "MouthPressRight", "MouthStretchLeft",
            "MouthStretchRight", "TongueOut", "TongueUp", "TongueDown", "TongueLeft", "TongueRight", "TongueRoll",
            "TongueBendDown", "TongueCurlUp", "TongueSquish", "TongueFlat", "TongueTwistLeft", "TongueTwistRight"
        ];
        var estimate = new MouthEstimate(.5f, [.. names.Select(name => new MouthExpression(name, .5f))]);

        // When
        var expressions = new ExpressionProcessor(ExpressionOptions.Default).Process(estimate, Seconds(0)).Expressions;

        // Then
        Assert.Equal(43, expressions.ProvidedCount);
    }

    [Fact]
    public void Process_RestingBias_IsRemovedAndForgottenOnReset()
    {
        // Given
        var processor = new ExpressionProcessor(new ExpressionOptions { Smoothing = false, Deadzone = 0 });
        var resting = new MouthEstimate(.1f, [new("MouthSmileLeft", .38f)]);
        for (var i = 0; i <= 40; i++) processor.Process(resting, Seconds(i * .1));

        // When
        var smiling = processor.Process(new MouthEstimate(.1f, [new("MouthSmileLeft", .69f)]), Seconds(4.1)).Expressions;

        // Then
        Assert.Equal(.38f, processor.BaselineOf("MouthSmileLeft")!.Value, 3);
        Assert.Equal(.5f, smiling[MouthCornerPullLeft]!.Value, 2);
        Assert.Equal(0f, processor.Process(resting, Seconds(4.2)).Expressions[MouthCornerPullLeft]!.Value, 3);

        // When
        processor.Reset();

        // Then
        Assert.Null(processor.BaselineOf("MouthSmileLeft"));
        Assert.Equal(.38f, processor.Process(resting, Seconds(4.3)).Expressions[MouthCornerPullLeft]);
    }

    [Fact]
    public void Process_LongSmile_DoesNotBecomeClosedMouthReference()
    {
        // Given: with this model a smile lowers JawOpen below a closed mouth (0.35 against 0.6 in the recorded session).
        var processor = new ExpressionProcessor(new ExpressionOptions { Smoothing = false, Deadzone = 0 });
        var ungated = new AdaptiveJawCalibration();
        var t = 0.0;
        void Feed(float jaw, float smile, double seconds)
        {
            for (var end = t + seconds; t < end; t += .05)
            {
                processor.Process(new MouthEstimate(jaw, [new("MouthSmileLeft", smile)]), Seconds(t));
                ungated.Observe(jaw, Seconds(t));
            }
        }
        for (var cycle = 0; cycle < 4; cycle++) { Feed(.6f, .38f, 1); Feed(1f, .38f, 1); }
        Feed(.35f, .95f, 8);

        // When
        var closed = processor.Process(new MouthEstimate(.6f, [new("MouthSmileLeft", .38f)]), Seconds(t));

        // Then
        Assert.True(closed.JawOpen!.Value.IsReady);
        Assert.InRange(closed.Expressions[JawOpen]!.Value, 0f, .05f);
        Assert.True(ungated.Observe(.6f, Seconds(t)).JawOpen > .1f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(.1f, 0f)]
    [InlineData(.15f, 0f)]
    [InlineData(.575f, .5f)]
    [InlineData(1f, 1f)]
    public void Process_DefaultDeadzone_ZeroesSmallValuesAndStretchesRest(float jaw, float expected)
    {
        // Given
        var processor = new ExpressionProcessor(new ExpressionOptions { AdaptiveCalibration = false, Smoothing = false });
        Assert.Equal(.15f, processor.Options.Deadzone);

        // When
        var expressions = processor.Process(new MouthEstimate(jaw), Seconds(0)).Expressions;

        // Then
        Assert.Equal(expected, expressions[JawOpen]!.Value, 4);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(.5f)]
    [InlineData(float.NaN)]
    public void Validate_DeadzoneOutsideZeroToHalf_FailsAndProcessorRejectsIt(float deadzone)
    {
        // Given
        var options = new ExpressionOptions { Deadzone = deadzone };

        // When
        var validated = options.Validate();

        // Then
        Assert.Equal("Deadzone", ResultAssert.Err(validated).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpressionProcessor(options));
    }

    [Fact]
    public void Process_JitteryInputWithSmoothing_SteadiesAndStaysInRange()
    {
        // Given
        var processor = new ExpressionProcessor(new ExpressionOptions { AdaptiveCalibration = false });
        var random = new Random(7);
        var outputs = new List<float>();

        // When
        for (var i = 0; i < 150; i++)
        {
            var raw = Math.Clamp(.5f + (float)(random.NextDouble() - .5) * .1f, 0f, 1f);
            outputs.Add(processor.Process(new MouthEstimate(raw), Seconds(i / 15.0)).Expressions[JawOpen]!.Value);
        }

        // Then
        var steps = outputs.Skip(30).Zip(outputs.Skip(31), (a, b) => Math.Abs(b - a)).Average();
        Assert.InRange(steps, 0, .015);
        Assert.All(outputs, value => Assert.InRange(value, 0f, 1f));
    }
}
