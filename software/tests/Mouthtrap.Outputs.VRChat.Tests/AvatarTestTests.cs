using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Outputs.VRChat.Tests;

public sealed class AvatarTestTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly int JawOpen = Array.IndexOf(Enumerable.Range(0, VrchatFaceOsc.PayloadLength).Select(VrchatFaceOsc.KeyAt).ToArray(),
        UnifiedExpression.JawOpen);

    [Fact]
    public async Task Run_Key_FollowsTheTriangleWaveAtTheRateThenSendsZeros()
    {
        // Given two packets a second and a two-second period
        var transport = new Transport();
        var clock = new FakeTimeProvider();
        var sent = new OscPacketCount();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // When it runs for four packets and is then stopped
        var run = AvatarTest.Run(transport.Resource(), [UnifiedExpression.JawOpen], rate: 2, period: 2, sent)
            .RunExitAsync(new TrackingEnvironment(clock), stop.Token).AsTask();
        for (var packet = 0; packet < 3; packet++)
        {
            await transport.Next();
            clock.Advance(TimeSpan.FromSeconds(.5));
        }
        await transport.Next();
        await stop.CancelAsync();
        var exit = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Interrupted, $"Got {exit}.");
        Assert.Equal([VrchatFaceOsc.Quantize(0), VrchatFaceOsc.Quantize(.5f), VrchatFaceOsc.Quantize(1), VrchatFaceOsc.Quantize(.5f), 0],
            transport.JawOpen);
        Assert.Equal(5, sent.Sent);
        Assert.True(transport.Released);
    }

    [Fact]
    public async Task Run_TransportFails_FailsWithOutputFailedAndReleasesIt()
    {
        // Given
        var transport = new Transport { Failing = true };

        // When
        var exit = await AvatarTest.Run(transport.Resource(), [UnifiedExpression.JawOpen], 30, 2)
            .RunExitAsync(new TrackingEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<OutputFailed>, $"Got {exit}.");
        Assert.True(transport.Released);
    }

    [Theory]
    [InlineData(.5, 2)]
    [InlineData(121, 2)]
    [InlineData(30, .1)]
    [InlineData(30, 61)]
    public void Run_RateOrPeriodOutOfRange_Throws(double rate, double period)
    {
        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => AvatarTest.Run(new Transport().Resource(), [UnifiedExpression.JawOpen], rate, period));
    }

    [Fact]
    public void Run_NoOrRepeatedKeys_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentException>(() => AvatarTest.Run(new Transport().Resource(), [], 30, 2));
        Assert.Throws<ArgumentException>(() => AvatarTest.Run(new Transport().Resource(), [UnifiedExpression.JawOpen, UnifiedExpression.JawOpen], 30, 2));
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(.5, .5f)]
    [InlineData(1, 1f)]
    [InlineData(1.5, .5f)]
    [InlineData(2, 0f)]
    [InlineData(2.5, .5f)]
    public void Triangle_TimeWithinPeriod_RisesAndFallsOncePerPeriod(double seconds, float expected)
    {
        // When
        var value = AvatarTest.Triangle(seconds, 2);

        // Then
        Assert.Equal(expected, value, 5);
    }

    // Records JawOpen of each packet, and signals each send so that the test advances time only after it.
    private sealed class Transport : IOscTransport
    {
        private readonly Channel<int> sends = Channel.CreateUnbounded<int>();

        public List<byte> JawOpen { get; } = [];
        public bool Failing { get; init; }
        public bool Released { get; private set; }

        public Resource<IOscTransport, OutputFailed, TrackingEnvironment> Resource() =>
            Moderato.Effects.Resource.Make(Effect<TrackingEnvironment>.Sync<IOscTransport>(() => this),
                (_, _) => Effect<TrackingEnvironment>.Sync(() => { Released = true; return Unit.Value; }));

        public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
        {
            if (Failing) return new Err<OutputFailed>(new("unreachable"));
            JawOpen.Add(ResultAssert.Ok(VrchatFaceOsc.ReadPayload(packet))[AvatarTestTests.JawOpen]);
            sends.Writer.TryWrite(0);
            return new Ok<Unit>(Unit.Value);
        }

        public async Task Next() =>
            await sends.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
    }
}
