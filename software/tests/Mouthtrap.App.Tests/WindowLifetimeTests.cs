using System.IO;
using System.Windows.Threading;
using Mouthtrap.App;
using Xunit;

namespace Mouthtrap.App.Tests;

public sealed class WindowLifetimeTests
{
    [Fact]
    public Task Close_IdleWindow_ClosesAfterClosingEventUnwinds()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Exception? failure = null;
                var closed = false;
                dispatcher.UnhandledException += (_, args) =>
                {
                    failure = args.Exception;
                    args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                // Given
                var window = new MainWindow(Path.Combine(Path.GetTempPath(), $"mouthtrap-{Guid.NewGuid():N}", "settings.json"));
                window.Closed += (_, _) => { closed = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };

                // When
                window.Close();
                Dispatcher.Run();

                // Then
                if (failure is not null) throw failure;
                Assert.True(closed);
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
