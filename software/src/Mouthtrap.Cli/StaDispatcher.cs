using System.Windows.Threading;

namespace Mouthtrap.Cli;

internal static class StaDispatcher
{
    public static Task<int> Run(Func<Dispatcher, Task<int>> action)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var result = 1;
                Exception? failure = null;
                _ = Execute();
                Dispatcher.Run();
                if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult(result);
                async Task Execute()
                {
                    try { result = await action(dispatcher); }
                    catch (Exception exception) { failure = exception; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
