using System.Net;
using ConsoleAppFramework;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;

namespace Mouthtrap.Cli;

// Where OSC goes: a UDP endpoint, or standard output for a dry run.
public sealed record OscTarget(OscEndpoint? Endpoint)
{
    public bool IsDryRun => Endpoint is null;
}

public static class OscOptions
{
    // Null when OSC output is not requested. Only IP literals are accepted, so nothing is resolved by name.
    // The error is a message for the command line.
    public static Result<OscTarget?, string> Parse(string? endpoint, bool dryRun)
    {
        if (dryRun && endpoint is not null) return new Err<string>("Use either --osc or the OSC dry run, not both.");
        if (dryRun) return new Ok<OscTarget?>(new(null));
        if (endpoint is null) return new Ok<OscTarget?>(null);
        return OscEndpoint.Create(endpoint) is Ok<OscEndpoint>(var parsed) ? new Ok<OscTarget?>(new(parsed))
            : new Err<string>("--osc must be an IP address and port, such as 127.0.0.1:9000.");
    }

    // One or more comma-separated Unified Expressions key names, such as "LipPuckerUpperLeft,LipPuckerUpperRight".
    public static Result<UnifiedExpression[], string> ParseKeys(string keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var parsed = new List<UnifiedExpression>();
        foreach (var name in keys.Split(',', StringSplitOptions.TrimEntries))
        {
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !Enum.TryParse(name, ignoreCase: false, out UnifiedExpression key) ||
                !Enum.IsDefined(key))
                return new Err<string>($"'{name}' is not a Unified Expressions key.");
            if (parsed.Contains(key)) return new Err<string>("Each key may be given once.");
            parsed.Add(key);
        }
        return new Ok<UnifiedExpression[]>([.. parsed]);
    }
}

public sealed partial class Commands
{
    /// <summary>Send a synthetic expression to VRChat over OSC, without a camera or model, to check the avatar.</summary>
    /// <param name="osc">VRChat's OSC input as IP:port, such as 127.0.0.1:9000.</param>
    /// <param name="dryRun">Print the OSC packets as JSON Lines instead of sending them.</param>
    /// <param name="key">Unified Expressions keys to move together, comma-separated, such as JawOpen or MouthCornerPullLeft,MouthCornerPullRight.</param>
    /// <param name="seconds">How long to send, 0 for unlimited (maximum 86400).</param>
    /// <param name="rate">Packets per second (1 to 120).</param>
    /// <param name="period">Seconds for one 0 → 1 → 0 cycle (0.2 to 60).</param>
    [Command("osc-test")]
    public async Task<int> OscTest(string? osc = null, bool dryRun = false, string key = "JawOpen", double seconds = 10,
        double rate = 30, double period = 2, CancellationToken cancellationToken = default)
    {
        var parsedTarget = OscOptions.Parse(osc, dryRun);
        if (parsedTarget is Err<string>(var targetError)) return Usage(targetError);
        if (parsedTarget.UnwrapOr(null) is not { } target) return Usage("Give --osc IP:port or the dry run.");
        var parsedKeys = OscOptions.ParseKeys(key);
        if (parsedKeys is Err<string>(var keyError)) return Usage(keyError);
        var expressions = parsedKeys.UnwrapOr([]);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) return Usage("seconds must be between 0 and 86400.");
        if (!double.IsFinite(rate) || rate is < AvatarTest.MinimumRate or > AvatarTest.MaximumRate) return Usage("rate must be between 1 and 120.");
        if (!double.IsFinite(period) || period is < AvatarTest.MinimumPeriod or > AvatarTest.MaximumPeriod) return Usage("period must be between 0.2 and 60.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var sent = new OscPacketCount();
        var program = AvatarTest.Run(OscTransport(target), expressions, rate, period, sent)
            .MapErr<TrackingError>(error => new TrackingOutputFailed(error));
        var exit = await program.RunExitAsync(new(TimeProvider.System), stop.Token);
        var code = exit switch
        {
            Ok<Unit> => 0,
            Interrupted => cancellationToken.IsCancellationRequested ? 130 : 0,
            Err<TrackingError>(var error) => ExpectedError(error),
            Die die => Defect(die)
        };
        Write(Console.Out, new
        {
            Type = "summary",
            ExitCode = code,
            Reason = code == 130 ? "cancelled" : code != 0 ? "failed" : "duration",
            Keys = expressions.Select(item => item.ToString()).ToArray(),
            OscSent = sent.Sent
        });
        return code;
    }

    // A UDP socket to the endpoint, or standard output for a dry run.
    private static Resource<IOscTransport, OutputFailed, TrackingEnvironment> OscTransport(OscTarget target) =>
        target.Endpoint is { } endpoint ? VrchatOscSession.Udp(endpoint)
            : Resource.Make(Effect<TrackingEnvironment>.Sync<IOscTransport>(() => new DryRunTransport()),
                (_, _) => Effect<TrackingEnvironment>.Sync(() => Unit.Value));

    // Prints what would be sent: the packet length and every nonzero byte by its VRChat key.
    private sealed class DryRunTransport : IOscTransport
    {
        public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
        {
            // The packet was written by VrchatFaceOutput, so failing to read it back is a bug, not an output failure.
            if (VrchatFaceOsc.ReadPayload(packet) is not Ok<byte[]>(var payload))
                throw new InvalidOperationException("The dry run could not read back its own packet.");
            var keys = new Dictionary<string, byte>();
            for (var offset = 0; offset < payload.Length; offset++)
                if (payload[offset] != 0) keys[VrchatFaceOsc.KeyAt(offset).ToString()] = payload[offset];
            Write(Console.Out, new { Type = "osc", VrchatFaceOsc.Address, Length = packet.Length, Keys = keys });
            return new Ok<Unit>(Unit.Value);
        }
    }
}
