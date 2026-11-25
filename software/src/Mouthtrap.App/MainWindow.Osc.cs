using System.Windows;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;

namespace Mouthtrap.App;

// The VRChat card's avatar test: moves JawOpen on the avatar for a few seconds without a camera or a model, as the
// CLI's osc-test does, so that the OSC setup can be checked on its own. The sending is AvatarTest's.
public partial class MainWindow
{
    public static readonly TimeSpan AvatarTestDuration = TimeSpan.FromSeconds(10);

    private Task? avatarTestTask;
    private CancellationTokenSource? avatarTestCancellation;

    private async void AvatarTest_Click(object sender, RoutedEventArgs e)
    {
        if (avatarTestCancellation is { } running)
        {
            running.Cancel();
            UpdateControls();
            return;
        }
        if (closing || sessionCancellation is not null || firmwareCancellation is not null) return;
        if (Core.OscEndpoint.Create(OscEndpoint.Text.Trim()) is not Ok<Core.OscEndpoint>(var endpoint))
        {
            OscStatusText.Text = Strings.Get("OscEndpointInvalid");
            return;
        }
        avatarTestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        avatarTestCancellation.CancelAfter(AvatarTestDuration);
        UpdateControls();
        avatarTestTask = RunAvatarTestAsync(endpoint, avatarTestCancellation);
        await avatarTestTask;
    }

    private async Task RunAvatarTestAsync(Core.OscEndpoint endpoint, CancellationTokenSource cancellation)
    {
        var sent = new OscPacketCount();
        try
        {
            OscStatusText.Text = Strings.Format("AvatarTestRunning", endpoint, AvatarTestDuration.TotalSeconds);
            var exit = await AvatarTest.Run(VrchatOscSession.Udp(endpoint), [UnifiedExpression.JawOpen], rate: 30, period: 2, sent)
                .RunExitAsync(new(clock), cancellation.Token);
            OscStatusText.Text = exit switch
            {
                // The test ends by its duration or by the button; either way it has done its job.
                Interrupted or Ok<Unit> => Strings.Format("AvatarTestDone", sent.Sent, endpoint),
                Err<OutputFailed>(var failure) => Strings.Format("OscFailed", failure.Message),
                Die die => ReportDefect(die),
            };
        }
        finally
        {
            cancellation.Dispose();
            avatarTestCancellation = null;
            UpdateControls();
        }
    }
}
