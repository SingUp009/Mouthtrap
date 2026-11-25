using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Outputs;

// Moves Unified Expressions keys on the avatar without a camera or a model, so that the OSC setup and the avatar can
// be checked on their own: each key follows a triangle wave from 0 up to 1 and back. The GUI and the CLI both run it.
public static class AvatarTest
{
    public const double MinimumRate = 1;
    public const double MaximumRate = 120;
    public const double MinimumPeriod = .2;
    public const double MaximumPeriod = 60;

    // Sends keys at rate packets per second until it is cancelled. The output, and its final all-zero packet, belong
    // to the run. The rate and the period are the caller's to check.
    public static Effect<Unit, OutputFailed, TrackingEnvironment> Run(Resource<IOscTransport, OutputFailed, TrackingEnvironment> transport,
        IReadOnlyList<UnifiedExpression> keys, double rate, double period, OscPacketCount? sent = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || keys.Distinct().Count() != keys.Count) throw new ArgumentException("Give each key once.", nameof(keys));
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, MinimumRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, MaximumRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, MinimumPeriod);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(period, MaximumPeriod);
        return Scope<OutputFailed, TrackingEnvironment>.Gen(async scope =>
        {
            var output = await scope.Acquire(VrchatOscSession.Output(transport, sent));
            var clock = scope.Env.Resolve<TimeProvider>();
            var startedAt = clock.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / rate), clock);
            do
            {
                var value = Triangle(clock.GetElapsedTime(startedAt).TotalSeconds, period);
                // The keys are distinct and the wave stays within 0..1, as ExpressionFrame.Create requires.
                var frame = ExpressionFrame.Create(keys.Select(key => new KeyValuePair<UnifiedExpression, float>(key, value)));
                if (output.Send(frame) is Err<OutputFailed>(var failure)) throw scope.Fail(failure);
            }
            while (await timer.WaitForNextTickAsync(scope.CancellationToken).ConfigureAwait(false));
            return Unit.Value;
        });
    }

    // A triangle wave from 0 up to 1 and back to 0 over each period, starting at 0.
    public static float Triangle(double seconds, double period)
    {
        var phase = seconds / period % 1;
        return (float)(1 - Math.Abs(2 * phase - 1));
    }
}
