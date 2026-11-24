using Mouthtrap.Core;
using Xunit;
using static Mouthtrap.Core.UnifiedExpression;

namespace Mouthtrap.Core.Tests;

public sealed class ExpressionMapTests
{
    // VRChat's tracking key order (creators.vrchat.com/avatars/face-tracking/osc/, updated 2026-10-26).
    private const string VrchatKeys =
        "EyeLookOutRight EyeLookInRight EyeLookUpRight EyeLookDownRight EyeLookOutLeft EyeLookInLeft EyeLookUpLeft " +
        "EyeLookDownLeft EyeClosedRight EyeClosedLeft EyeDilationRight EyeDilationLeft EyeConstrictRight EyeConstrictLeft " +
        "EyeSquintRight EyeSquintLeft EyeWideRight EyeWideLeft BrowPinchRight BrowPinchLeft BrowLowererRight BrowLowererLeft " +
        "BrowInnerUpRight BrowInnerUpLeft BrowOuterUpRight BrowOuterUpLeft NasalDilationRight NasalDilationLeft " +
        "NasalConstrictRight NasalConstrictLeft CheekSquintRight CheekSquintLeft CheekPuffRight CheekPuffLeft CheekSuckRight " +
        "CheekSuckLeft JawOpen JawRight JawLeft JawForward JawBackward JawClench JawMandibleRaise MouthClosed " +
        "LipSuckUpperRight LipSuckUpperLeft LipSuckLowerRight LipSuckLowerLeft LipSuckCornerRight LipSuckCornerLeft " +
        "LipFunnelUpperRight LipFunnelUpperLeft LipFunnelLowerRight LipFunnelLowerLeft LipPuckerUpperRight LipPuckerUpperLeft " +
        "LipPuckerLowerRight LipPuckerLowerLeft MouthUpperUpRight MouthUpperUpLeft MouthUpperDeepenRight MouthUpperDeepenLeft " +
        "NoseSneerRight NoseSneerLeft MouthLowerDownRight MouthLowerDownLeft MouthUpperRight MouthUpperLeft MouthLowerRight " +
        "MouthLowerLeft MouthCornerPullRight MouthCornerPullLeft MouthCornerSlantRight MouthCornerSlantLeft MouthFrownRight " +
        "MouthFrownLeft MouthStretchRight MouthStretchLeft MouthDimpleRight MouthDimpleLeft MouthRaiserUpper MouthRaiserLower " +
        "MouthPressRight MouthPressLeft MouthTightenerRight MouthTightenerLeft TongueOut TongueUp TongueDown TongueRight " +
        "TongueLeft TongueRoll TongueBendDown TongueCurlUp TongueSquish TongueFlat TongueTwistRight TongueTwistLeft " +
        "SoftPalateClose ThroatSwallow NeckFlexRight NeckFlexLeft";

    // The 44 additional outputs of the Babble model, in output order (JawOpen is output 4).
    private static readonly string[] BabbleOutputs =
    [
        "CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight", "JawForward", "JawLeft", "JawRight",
        "NoseSneerLeft", "NoseSneerRight", "MouthFunnel", "MouthPucker", "MouthLeft", "MouthRight", "MouthRollUpper",
        "MouthRollLower", "MouthShrugUpper", "MouthShrugLower", "MouthClose", "MouthSmileLeft", "MouthSmileRight",
        "MouthFrownLeft", "MouthFrownRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthUpperUpLeft", "MouthUpperUpRight",
        "MouthLowerDownLeft", "MouthLowerDownRight", "MouthPressLeft", "MouthPressRight", "MouthStretchLeft",
        "MouthStretchRight", "TongueOut", "TongueUp", "TongueDown", "TongueLeft", "TongueRight", "TongueRoll",
        "TongueBendDown", "TongueCurlUp", "TongueSquish", "TongueFlat", "TongueTwistLeft", "TongueTwistRight"
    ];

    [Fact]
    public void UnifiedExpression_KeySet_IsExactlyVrchats102TrackingKeys()
    {
        // When / Then
        Assert.Equal(VrchatKeys.Split(' '), Enum.GetNames<UnifiedExpression>());
        Assert.Equal(102, ExpressionFrame.KeyCount);
    }

    [Theory]
    [InlineData("MouthClose", new[] { MouthClosed })]
    [InlineData("MouthShrugUpper", new[] { MouthRaiserUpper })]
    [InlineData("MouthShrugLower", new[] { MouthRaiserLower })]
    [InlineData("MouthFunnel", new[] { LipFunnelUpperRight, LipFunnelUpperLeft, LipFunnelLowerRight, LipFunnelLowerLeft })]
    [InlineData("MouthPucker", new[] { LipPuckerUpperRight, LipPuckerUpperLeft, LipPuckerLowerRight, LipPuckerLowerLeft })]
    [InlineData("MouthRollUpper", new[] { LipSuckUpperRight, LipSuckUpperLeft })]
    [InlineData("MouthRollLower", new[] { LipSuckLowerRight, LipSuckLowerLeft })]
    [InlineData("MouthLeft", new[] { MouthUpperLeft, MouthLowerLeft })]
    [InlineData("MouthRight", new[] { MouthUpperRight, MouthLowerRight })]
    [InlineData("MouthSmileLeft", new[] { MouthCornerPullLeft })]
    [InlineData("MouthSmileRight", new[] { MouthCornerPullRight })]
    [InlineData("TongueTwistLeft", new[] { TongueTwistLeft })]
    public void TargetsOf_RenamedOrFannedOutOutput_FollowsMappingTable(string name, UnifiedExpression[] targets)
    {
        // When
        var mapped = ArkitExpressionMap.TargetsOf(name);

        // Then
        Assert.Equal(targets, mapped);
    }

    [Fact]
    public void Map_AllBabbleOutputs_Fills55KeysAndLeaves47Unprovided()
    {
        // Given
        Assert.Equal(44, BabbleOutputs.Distinct().Count());
        Assert.All(BabbleOutputs, name => Assert.True(ArkitExpressionMap.IsKnown(name), name));
        KeyValuePair<string, float>[] outputs = [new("JawOpen", .75f), .. BabbleOutputs.Select(name => new KeyValuePair<string, float>(name, .5f))];

        // When
        var frame = ArkitExpressionMap.Map(outputs);

        // Then
        Assert.Equal(55, frame.ProvidedCount);
        Assert.Equal(.75f, frame[JawOpen]);
        Assert.Equal(.5f, frame[LipFunnelLowerLeft]);
        UnifiedExpression[] unprovided =
        [
            .. Enum.GetValues<UnifiedExpression>().Where(key => key <= BrowOuterUpLeft),
            NasalDilationRight, NasalDilationLeft, NasalConstrictRight, NasalConstrictLeft, CheekSquintRight, CheekSquintLeft,
            JawBackward, JawClench, JawMandibleRaise, LipSuckCornerRight, LipSuckCornerLeft,
            MouthUpperDeepenRight, MouthUpperDeepenLeft, MouthCornerSlantRight, MouthCornerSlantLeft,
            MouthTightenerRight, MouthTightenerLeft, SoftPalateClose, ThroatSwallow, NeckFlexRight, NeckFlexLeft
        ];
        Assert.Equal(47, unprovided.Length);
        Assert.All(unprovided, key => Assert.Null(frame[key]));
    }

    [Fact]
    public void Map_ZeroAndAbsentOutputs_KeepsZeroProvidedAndLeavesAbsentUnprovided()
    {
        // Given
        KeyValuePair<string, float>[] outputs = [new("JawOpen", 1f), new("MouthSmileLeft", 1f), new("TongueOut", 0f)];

        // When
        var frame = ArkitExpressionMap.Map(outputs);

        // Then
        Assert.Equal(1f, frame[JawOpen]);
        Assert.Equal(1f, frame[MouthCornerPullLeft]);
        Assert.Equal(0f, frame[TongueOut]);
        Assert.True(frame.IsProvided(TongueOut));
        Assert.False(frame.IsProvided(MouthCornerPullRight));
        Assert.Equal([JawOpen, MouthCornerPullLeft, TongueOut], frame.Provided.Select(item => item.Key));
    }

    [Theory]
    [InlineData("JawOpen", true)]
    [InlineData("MouthSmile", false)]
    [InlineData("mouthSmileLeft", false)]
    [InlineData("MouthCornerPullLeft", false)]
    public void IsKnown_Name_AcceptsOnlyArkitStyleNames(string name, bool known)
    {
        // When / Then
        Assert.Equal(known, ArkitExpressionMap.IsKnown(name));
        if (!known) Assert.Throws<ArgumentException>(() => ArkitExpressionMap.TargetsOf(name));
    }

    [Fact]
    public void Create_OutOfRangeDuplicateOrUndefinedKey_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionFrame.Create([new(JawOpen, float.NaN)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionFrame.Create([new(JawOpen, 1.01f)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionFrame.Create([new(JawOpen, -.01f)]));
        Assert.Throws<ArgumentException>(() => ExpressionFrame.Create([new(JawOpen, .1f), new(JawOpen, .2f)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionFrame.Create([new((UnifiedExpression)102, .1f)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpressionFrame.Empty[(UnifiedExpression)(-1)]);
        Assert.Equal(0, ExpressionFrame.Empty.ProvidedCount);
        Assert.Equal(1, ExpressionFrame.Create([new(NeckFlexLeft, 0f)]).ProvidedCount);
    }
}
