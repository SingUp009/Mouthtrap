using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Mouthtrap.Core;

// Maps model outputs named after ARKit blendshapes, as Babble's are, to Unified Expressions.
// A fanned-out output sends the same value to every target,
// and a smile drives MouthCornerPull only, as upstream Baballonia does.
public static class ArkitExpressionMap
{
    private static readonly FrozenDictionary<ArkitShape, ImmutableArray<UnifiedExpression>> Targets = Build();

    // The shape a model output name stands for, matched exactly. Model definitions are read with this, so an
    // unknown name is rejected when the model loads.
    public static bool TryParse(string name, out ArkitShape shape)
    {
        ArgumentNullException.ThrowIfNull(name);
        shape = default;
        return name.Length > 0 && char.IsAsciiLetter(name[0]) && Enum.TryParse(name, ignoreCase: false, out shape) && Enum.IsDefined(shape);
    }

    // Tongue outputs react to an open mouth with the Babble model and a front camera, so they can be left out.
    public static bool IsTongue(ArkitShape shape) => shape is >= ArkitShape.TongueOut and <= ArkitShape.TongueTwistRight;

    public static ImmutableArray<UnifiedExpression> TargetsOf(ArkitShape shape) =>
        Targets.TryGetValue(shape, out var targets) ? targets : throw new ArgumentOutOfRangeException(nameof(shape));

    // Maps outputs (JawOpen included) and their 0..1 values; outputs left out stay unprovided.
    public static ExpressionFrame Map(IEnumerable<KeyValuePair<ArkitShape, float>> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var provided = new List<KeyValuePair<UnifiedExpression, float>>();
        foreach (var (shape, value) in outputs)
            foreach (var target in TargetsOf(shape))
                provided.Add(new(target, value));
        return ExpressionFrame.Create(provided);
    }

    private static FrozenDictionary<ArkitShape, ImmutableArray<UnifiedExpression>> Build()
    {
        var map = new Dictionary<ArkitShape, ImmutableArray<UnifiedExpression>>
        {
            // Renamed or fanned out.
            [ArkitShape.MouthClose] = [UnifiedExpression.MouthClosed],
            [ArkitShape.MouthShrugUpper] = [UnifiedExpression.MouthRaiserUpper],
            [ArkitShape.MouthShrugLower] = [UnifiedExpression.MouthRaiserLower],
            [ArkitShape.MouthFunnel] = [UnifiedExpression.LipFunnelUpperRight, UnifiedExpression.LipFunnelUpperLeft,
                UnifiedExpression.LipFunnelLowerRight, UnifiedExpression.LipFunnelLowerLeft],
            [ArkitShape.MouthPucker] = [UnifiedExpression.LipPuckerUpperRight, UnifiedExpression.LipPuckerUpperLeft,
                UnifiedExpression.LipPuckerLowerRight, UnifiedExpression.LipPuckerLowerLeft],
            [ArkitShape.MouthRollUpper] = [UnifiedExpression.LipSuckUpperRight, UnifiedExpression.LipSuckUpperLeft],
            [ArkitShape.MouthRollLower] = [UnifiedExpression.LipSuckLowerRight, UnifiedExpression.LipSuckLowerLeft],
            [ArkitShape.MouthLeft] = [UnifiedExpression.MouthUpperLeft, UnifiedExpression.MouthLowerLeft],
            [ArkitShape.MouthRight] = [UnifiedExpression.MouthUpperRight, UnifiedExpression.MouthLowerRight],
            [ArkitShape.MouthSmileLeft] = [UnifiedExpression.MouthCornerPullLeft],
            [ArkitShape.MouthSmileRight] = [UnifiedExpression.MouthCornerPullRight]
        };
        // Every other shape has the same name in both standards.
        foreach (var shape in Enum.GetValues<ArkitShape>())
            if (!map.ContainsKey(shape)) map[shape] = [Enum.Parse<UnifiedExpression>(shape.ToString())];
        return map.ToFrozenDictionary();
    }
}
