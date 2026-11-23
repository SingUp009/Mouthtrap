namespace Mouthtrap.Core;

public readonly record struct CalibrationSnapshot(bool IsReady, float? JawOpen, float? Lower, float? Upper);

// Single inference-loop owner; the host supplies monotonic session time.
public sealed class AdaptiveJawCalibration
{
    private const float MinimumSpan = .25f;
    private readonly (TimeSpan At, float Value)[] samples = new (TimeSpan, float)[128];
    private readonly float[] sorted = new float[128];
    private int head, count;
    private TimeSpan? lastAt;
    private TimeSpan nextSample, nextEvaluation;
    private float lower, upper, seedLower, seedUpper;
    private bool ready;

    public void Reset()
    {
        head = count = 0;
        lastAt = null;
        nextSample = nextEvaluation = TimeSpan.Zero;
        ready = false;
    }

    // learn is false for frames that must not shape the range, such as smiles, which lower this model's JawOpen
    // below a closed mouth; they are still mapped with the current range.
    public CalibrationSnapshot Observe(float rawJawOpen, TimeSpan now, bool learn = true)
    {
        if (!float.IsFinite(rawJawOpen)) throw new ArgumentOutOfRangeException(nameof(rawJawOpen));
        if (now < TimeSpan.Zero || lastAt is { } previous && now < previous)
            throw new ArgumentOutOfRangeException(nameof(now), "Session time must be monotonic.");
        lastAt = now;
        var value = Math.Clamp(rawJawOpen, 0f, 1f);
        while (count > 0 && now - samples[head].At > TimeSpan.FromSeconds(10))
        { head = (head + 1) % samples.Length; count--; }
        if (learn && now >= nextSample)
        {
            if (count == samples.Length) { head = (head + 1) % samples.Length; count--; }
            samples[(head + count++) % samples.Length] = (now, value);
            nextSample = now + TimeSpan.FromMilliseconds(100);
        }
        if (now >= nextEvaluation)
        {
            nextEvaluation = now + TimeSpan.FromSeconds(1);
            if (count >= 20 && now - samples[head].At >= TimeSpan.FromSeconds(2)) UpdateRange();
        }
        return ready ? new(true, Math.Clamp((value - lower) / (upper - lower), 0f, 1f), lower, upper)
            : new(false, null, null, null);
    }

    private void UpdateRange()
    {
        for (var i = 0; i < count; i++) sorted[i] = samples[(head + i) % samples.Length].Value;
        Array.Sort(sorted, 0, count);
        var candidateLower = Quantile(.1);
        var candidateUpper = Quantile(.9);
        var span = candidateUpper - candidateLower;
        if (span < MinimumSpan) return;
        var lowRun = 0;
        var highRun = 0;
        var sawLow = false;
        var sawHigh = false;
        for (var i = 0; i < count; i++)
        {
            var value = samples[(head + i) % samples.Length].Value;
            lowRun = value <= candidateLower + span * .1f ? lowRun + 1 : 0;
            highRun = value >= candidateUpper - span * .1f ? highRun + 1 : 0;
            sawLow |= lowRun >= 3;
            sawHigh |= highRun >= 3;
        }
        if (!sawLow || !sawHigh) return;
        if (!ready)
        {
            lower = seedLower = candidateLower;
            upper = seedUpper = candidateUpper;
            ready = true;
            return;
        }
        // Both ends must remain recognizable; a held expression is not a new zero.
        if (candidateLower > lower + (upper - lower) * .2f || candidateUpper < upper - (upper - lower) * .2f) return;
        // shortcut: Scalar evidence cannot validate pose; add quality gating before unattended reinitialization.
        var allowance = (seedUpper - seedLower) * .25f;
        var targetLower = Math.Clamp(candidateLower, Math.Max(0, seedLower - allowance), seedLower + allowance);
        var targetUpper = Math.Clamp(candidateUpper, seedUpper - allowance, Math.Min(1, seedUpper + allowance));
        if (targetUpper - targetLower < MinimumSpan) return;
        lower += Math.Clamp(targetLower - lower, -.02f, .02f);
        upper += Math.Clamp(targetUpper - upper, -.02f, .02f);
    }

    private float Quantile(double probability)
    {
        var at = (count - 1) * probability;
        var left = (int)at;
        var right = (int)Math.Ceiling(at);
        return sorted[left] + (float)(at - left) * (sorted[right] - sorted[left]);
    }
}
