using Moderato.Results;

namespace Mouthtrap.Core;

// How model outputs become the expressions that are sent.
public sealed record ExpressionOptions
{
    // Learn each additional output's resting level and map raw values above it to 0..1.
    public bool AdaptiveCalibration { get; init; } = true;
    // Smooth every output with a One Euro filter.
    public bool Smoothing { get; init; } = true;
    // Tongue outputs react to an open mouth with the Babble model and a front camera,
    // so they are left unprovided unless asked for.
    public bool Tongue { get; init; }
    public Deadzone Deadzone { get; init; } = Deadzone.Default;

    public static ExpressionOptions Default { get; } = new();
}

public readonly record struct ExpressionResult(ExpressionFrame Expressions, CalibrationSnapshot? JawOpen);

// Turns one session's model outputs into Unified Expressions: JawOpen range and per-output baseline calibration,
// smoothing, then the ARKit-style map. Owned by the single tracking loop; the host supplies monotonic session time.
public sealed class ExpressionProcessor(ExpressionOptions options)
{
    // A calibrated smile above this keeps the frame out of JawOpen's range: with this model a smile lowers JawOpen
    // below a closed mouth, which would otherwise become the closed reference.
    public const float SmileGate = .3f;

    // Smoothing tuned on a recorded session at about 15 fps: jitter while holding an expression fell to about 30 %
    // with about two frames of added delay on fast changes.
    public const float MinCutoff = .7f;
    public const float Beta = 3f;

    private readonly Dictionary<ArkitShape, AdaptiveBaseline> baselines = [];
    private readonly Dictionary<ArkitShape, OneEuroFilter> filters = [];
    private readonly List<KeyValuePair<ArkitShape, float>> values = [];
    private readonly AdaptiveJawCalibration jaw = new();

    public ExpressionOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public void Reset()
    {
        baselines.Clear();
        filters.Clear();
        jaw.Reset();
    }

    // The learned resting level of an output, or null before it is learned or when calibration is off.
    public float? BaselineOf(ArkitShape shape) => baselines.TryGetValue(shape, out var baseline) ? baseline.Level : null;

    public ExpressionResult Process(MouthEstimate estimate, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        values.Clear();
        float? smile = null;
        foreach (var expression in estimate.AdditionalExpressions)
        {
            if (!Options.Tongue && ArkitExpressionMap.IsTongue(expression.Shape)) continue;
            var value = expression.Value;
            if (Options.AdaptiveCalibration)
            {
                if (!baselines.TryGetValue(expression.Shape, out var baseline)) baselines[expression.Shape] = baseline = new();
                value = baseline.Observe(expression.Raw, now);
                if (expression.Shape is ArkitShape.MouthSmileLeft or ArkitShape.MouthSmileRight && baseline.Level is not null)
                    smile = Math.Max(smile ?? 0f, value);
            }
            values.Add(new(expression.Shape, value));
        }
        CalibrationSnapshot? snapshot = Options.AdaptiveCalibration
            ? jaw.Observe(estimate.RawJawOpen, now, learn: smile is not { } level || level < SmileGate)
            : null;
        values.Add(new(ArkitShape.JawOpen, snapshot?.JawOpen ?? estimate.JawOpen));
        for (var i = 0; i < values.Count; i++)
            values[i] = new(values[i].Key, Options.Deadzone.Apply(Smooth(values[i].Key, values[i].Value, now)));
        return new(ArkitExpressionMap.Map(values), snapshot);
    }

    private float Smooth(ArkitShape shape, float value, TimeSpan now)
    {
        if (!Options.Smoothing) return value;
        if (!filters.TryGetValue(shape, out var filter)) filters[shape] = filter = new(MinCutoff, Beta);
        return Math.Clamp(filter.Filter(value, now), 0f, 1f);
    }
}

// Tracks the low end of an output over the last 30 seconds as its resting level. The 20th percentile ignores brief
// dips, and expressions held for most of the window raise it, which is the trade-off for following session drift.
public sealed class AdaptiveBaseline
{
    public const double Quantile = .2;
    private const int Capacity = 300;
    private const int MinimumSamples = 30;
    private const float MaximumLevel = .9f;
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EvaluateEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly (TimeSpan At, float Value)[] samples = new (TimeSpan, float)[Capacity];
    private readonly float[] sorted = new float[Capacity];
    private int head, count;
    private TimeSpan? lastAt;
    private TimeSpan nextSample, nextEvaluation;

    // Null until enough samples have been seen.
    public float? Level { get; private set; }

    // Returns raw mapped to 0..1 above the resting level, or the clamped raw value until a level is learned.
    public float Observe(float raw, TimeSpan now)
    {
        if (!float.IsFinite(raw)) throw new ArgumentOutOfRangeException(nameof(raw));
        if (now < TimeSpan.Zero || lastAt is { } previous && now < previous)
            throw new ArgumentOutOfRangeException(nameof(now), "Session time must be monotonic.");
        lastAt = now;
        while (count > 0 && now - samples[head].At > Window) { head = (head + 1) % Capacity; count--; }
        if (now >= nextSample)
        {
            if (count == Capacity) { head = (head + 1) % Capacity; count--; }
            samples[(head + count++) % Capacity] = (now, raw);
            nextSample = now + SampleEvery;
        }
        if (now >= nextEvaluation && count >= MinimumSamples)
        {
            nextEvaluation = now + EvaluateEvery;
            for (var i = 0; i < count; i++) sorted[i] = samples[(head + i) % Capacity].Value;
            Array.Sort(sorted, 0, count);
            var at = (count - 1) * Quantile;
            var low = (int)at;
            var high = Math.Min(low + 1, count - 1);
            Level = Math.Clamp(sorted[low] + (float)(at - low) * (sorted[high] - sorted[low]), 0f, MaximumLevel);
        }
        return Level is { } level ? Math.Clamp((raw - level) / (1f - level), 0f, 1f) : Math.Clamp(raw, 0f, 1f);
    }
}

// The One Euro filter (Casiez, Roussel and Vogel, CHI 2012): a low-pass filter whose cutoff rises with speed, so
// still values are steadied while fast movements keep little delay.
public sealed class OneEuroFilter(float minCutoff, float beta, float derivativeCutoff = 1f)
{
    private float value, derivative;
    private TimeSpan? lastAt;

    public float Filter(float input, TimeSpan now)
    {
        if (!float.IsFinite(input)) throw new ArgumentOutOfRangeException(nameof(input));
        if (lastAt is not { } previous)
        {
            lastAt = now;
            return value = input;
        }
        if (now < previous) throw new ArgumentOutOfRangeException(nameof(now), "Session time must be monotonic.");
        var elapsed = (float)(now - previous).TotalSeconds;
        if (elapsed <= 0f) return value;
        lastAt = now;
        derivative += Alpha(derivativeCutoff, elapsed) * ((input - value) / elapsed - derivative);
        var cutoff = minCutoff + beta * MathF.Abs(derivative);
        return value += Alpha(cutoff, elapsed) * (input - value);
    }

    private static float Alpha(float cutoff, float elapsed) => 1f / (1f + 1f / (2f * MathF.PI * cutoff * elapsed));
}
