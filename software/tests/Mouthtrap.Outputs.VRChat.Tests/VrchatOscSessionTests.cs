using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Outputs.VRChat.Tests;

public sealed class VrchatOscSessionTests
{
    private static readonly ExpressionFrame Open = ExpressionFrame.Create([new(UnifiedExpression.JawOpen, 1f)]);

    [Fact]
    public async Task Output_FramePublished_SendsItThenZerosBeforeReleaseAndCountsBoth()
    {
        // Given
        var transport = new RecordingTransport();
        var count = new OscPacketCount();

        // When
        var exit = await VrchatOscSession.Output(transport.Resource(), count)
            .Use(output => Effect<PreviewEnvironment>.FromResult(output.Publish(Frame(Open))))
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Ok<Unit>);
        Assert.Equal(["send 36=255", "send zeros", "release"], transport.Events);
        Assert.Equal(2, count.Sent);
    }

    [Fact]
    public async Task Output_UseFails_SendsZerosAndKeepsError()
    {
        // Given
        var transport = new RecordingTransport();

        // When
        var exit = await VrchatOscSession.Output(transport.Resource())
            .Use(_ => Scope<OutputFailed, PreviewEnvironment>.Gen<Unit>(async scope =>
            {
                await Task.Yield();
                throw scope.Fail(new OutputFailed("gone"));
            }))
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<OutputFailed>(OutputFailed("gone", _)));
        Assert.Equal(["send zeros", "release"], transport.Events);
    }

    [Fact]
    public async Task Output_UseCancelled_SendsZeros()
    {
        // Given
        var transport = new RecordingTransport();
        using var cancellation = new CancellationTokenSource();

        // When
        var exit = await VrchatOscSession.Output(transport.Resource())
            .Use(_ => Effect<PreviewEnvironment>.Async(async token =>
            {
                await cancellation.CancelAsync();
                await new TaskCompletionSource().Task.WaitAsync(token);
                return Unit.Value;
            }))
            .RunExitAsync(new(TimeProvider.System), cancellation.Token);

        // Then
        Assert.True(exit is Interrupted);
        Assert.Equal(["send zeros", "release"], transport.Events);
    }

    [Fact]
    public async Task Output_SendFails_ReturnsTheOutputErrorAndReleases()
    {
        // Given
        var transport = new RecordingTransport { Failing = true };

        // When
        var exit = await VrchatOscSession.Output(transport.Resource())
            .Use(output => Effect<PreviewEnvironment>.FromResult(output.Publish(Frame(Open))))
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<OutputFailed>(OutputFailed("unreachable", _)));
        Assert.Equal(["release"], transport.Events);
    }

    [Fact]
    public async Task Output_TransportCannotOpen_SkipsUse()
    {
        // Given
        var ran = false;
        var failing = Resource.Make(Effect<PreviewEnvironment>.Try<SocketLikeException, IOscTransport, OutputFailed>(
                () => throw new SocketLikeException(), _ => new OutputFailed("no socket", 10013)),
            (_, _) => Effect<PreviewEnvironment>.Sync(() => Unit.Value));

        // When
        var exit = await VrchatOscSession.Output(failing)
            .Use(_ => Effect<PreviewEnvironment>.Sync(() => { ran = true; return Unit.Value; }))
            .RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<OutputFailed>(OutputFailed(_, 10013)));
        Assert.False(ran);
    }

    private static TrackingFrame Frame(ExpressionFrame expressions) =>
        new(new(new MouthEstimate(0f), expressions, null),
            new(new Rgb24FrameImage(new byte[3], 1, 1, 3), 0, new(PreprocessSettings.Default, 0), new byte[3], 1, 1, 0, null, 0, 1,
                new(1, 0)));

    private sealed class SocketLikeException : Exception;

    private sealed class RecordingTransport : IOscTransport
    {
        public List<string> Events { get; } = [];
        public bool Failing { get; init; }

        public Resource<IOscTransport, OutputFailed, PreviewEnvironment> Resource() =>
            Moderato.Effects.Resource.Make(Effect<PreviewEnvironment>.Sync<IOscTransport>(() => this),
                (_, _) => Effect<PreviewEnvironment>.Sync(() => { Events.Add("release"); return Unit.Value; }));

        public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
        {
            if (Failing) return new Err<OutputFailed>(new("unreachable"));
            var payload = ResultAssert.Ok(VrchatFaceOsc.ReadPayload(packet));
            var keys = Enumerable.Range(0, payload.Length).Where(offset => payload[offset] != 0).Select(offset => $"{offset}={payload[offset]}").ToArray();
            Events.Add(keys.Length == 0 ? "send zeros" : "send " + string.Join(",", keys));
            return new Ok<Unit>(Unit.Value);
        }
    }
}
