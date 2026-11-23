using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Mouthtrap.Core;

// Maps model outputs named after ARKit blendshapes, as Babble's are, to Unified Expressions.
// A fanned-out output sends the same value to every target,
// and a smile drives MouthCornerPull only, as upstream Baballonia does.
public static class ArkitExpressionMap
{
    private static readonly FrozenDictionary<string, ImmutableArray<UnifiedExpression>> Targets = Build();

    public static IEnumerable<string> Names => Targets.Keys;

    public static bool IsKnown(string name) => Targets.ContainsKey(name);

    public static ImmutableArray<UnifiedExpression> TargetsOf(string name) =>
        Targets.TryGetValue(name, out var targets) ? targets
            : throw new ArgumentException($"'{name}' is not an ARKit-style expression name.", nameof(name));

    // jawOpen is the JawOpen value to send, calibrated or not; additional outputs are sent clamped.
    public static ExpressionFrame Map(MouthEstimate estimate, float jawOpen)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return Map([new("JawOpen", jawOpen), .. estimate.AdditionalExpressions.Select(item => new KeyValuePair<string, float>(item.Name, item.Value))]);
    }

    // Maps output names (JawOpen included) and their 0..1 values; outputs left out stay unprovided.
    public static ExpressionFrame Map(IEnumerable<KeyValuePair<string, float>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var provided = new List<KeyValuePair<UnifiedExpression, float>>();
        foreach (var (name, value) in outputs)
            foreach (var target in TargetsOf(name))
                provided.Add(new(target, value));
        return ExpressionFrame.Create(provided);
    }

    private static FrozenDictionary<string, ImmutableArray<UnifiedExpression>> Build()
    {
        // Same name in both standards.
        UnifiedExpression[] identical =
        [
            UnifiedExpression.CheekPuffLeft, UnifiedExpression.CheekPuffRight,
            UnifiedExpression.CheekSuckLeft, UnifiedExpression.CheekSuckRight,
            UnifiedExpression.JawOpen, UnifiedExpression.JawForward, UnifiedExpression.JawLeft, UnifiedExpression.JawRight,
            UnifiedExpression.NoseSneerLeft, UnifiedExpression.NoseSneerRight,
            UnifiedExpression.MouthFrownLeft, UnifiedExpression.MouthFrownRight,
            UnifiedExpression.MouthDimpleLeft, UnifiedExpression.MouthDimpleRight,
            UnifiedExpression.MouthUpperUpLeft, UnifiedExpression.MouthUpperUpRight,
            UnifiedExpression.MouthLowerDownLeft, UnifiedExpression.MouthLowerDownRight,
            UnifiedExpression.MouthPressLeft, UnifiedExpression.MouthPressRight,
            UnifiedExpression.MouthStretchLeft, UnifiedExpression.MouthStretchRight,
            UnifiedExpression.TongueOut, UnifiedExpression.TongueUp, UnifiedExpression.TongueDown,
            UnifiedExpression.TongueLeft, UnifiedExpression.TongueRight, UnifiedExpression.TongueRoll,
            UnifiedExpression.TongueBendDown, UnifiedExpression.TongueCurlUp,
            UnifiedExpression.TongueSquish, UnifiedExpression.TongueFlat,
            UnifiedExpression.TongueTwistLeft, UnifiedExpression.TongueTwistRight
        ];
        var map = identical.ToDictionary(key => key.ToString(), key => ImmutableArray.Create(key), StringComparer.Ordinal);
        // Renamed or fanned out.
        map.Add("MouthClose", [UnifiedExpression.MouthClosed]);
        map.Add("MouthShrugUpper", [UnifiedExpression.MouthRaiserUpper]);
        map.Add("MouthShrugLower", [UnifiedExpression.MouthRaiserLower]);
        map.Add("MouthFunnel", [UnifiedExpression.LipFunnelUpperRight, UnifiedExpression.LipFunnelUpperLeft,
            UnifiedExpression.LipFunnelLowerRight, UnifiedExpression.LipFunnelLowerLeft]);
        map.Add("MouthPucker", [UnifiedExpression.LipPuckerUpperRight, UnifiedExpression.LipPuckerUpperLeft,
            UnifiedExpression.LipPuckerLowerRight, UnifiedExpression.LipPuckerLowerLeft]);
        map.Add("MouthRollUpper", [UnifiedExpression.LipSuckUpperRight, UnifiedExpression.LipSuckUpperLeft]);
        map.Add("MouthRollLower", [UnifiedExpression.LipSuckLowerRight, UnifiedExpression.LipSuckLowerLeft]);
        map.Add("MouthLeft", [UnifiedExpression.MouthUpperLeft, UnifiedExpression.MouthLowerLeft]);
        map.Add("MouthRight", [UnifiedExpression.MouthUpperRight, UnifiedExpression.MouthLowerRight]);
        map.Add("MouthSmileLeft", [UnifiedExpression.MouthCornerPullLeft]);
        map.Add("MouthSmileRight", [UnifiedExpression.MouthCornerPullRight]);
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
