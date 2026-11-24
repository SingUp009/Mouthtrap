namespace Mouthtrap.Core;

// Wakes one waiter when something it watches changes. A Set with no waiter is kept until the next wait, so a change
// that lands between checking and waiting is not missed; several Sets before a wait count once.
internal sealed class WakeSignal
{
    private readonly object gate = new();
    private TaskCompletionSource? waiter;
    private bool pending;

    public void Set()
    {
        TaskCompletionSource? woken;
        lock (gate)
        {
            woken = waiter;
            waiter = null;
            if (woken is null) pending = true;
        }
        woken?.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (pending)
            {
                pending = false;
                return Task.CompletedTask;
            }
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return waiter.Task.WaitAsync(cancellationToken);
        }
    }
}
