using Microsoft.Extensions.Time.Testing;

namespace Mouthtrap.Tests;

// A FakeTimeProvider that also reports when the code under test creates a timer, so a test advances time only after
// the timeout it wants to trigger exists, instead of waiting a real delay.
internal sealed class TimerAwareTimeProvider : FakeTimeProvider
{
    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        TimerCreated.TrySetResult();
        return timer;
    }
}
