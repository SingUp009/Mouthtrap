using System.Net;
using System.Net.Sockets;
using System.Text;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Outputs.VRChat.Tests;

public sealed class VrchatFaceOscTests
{
    [Fact]
    public void EncodePayload_JawOpenAtOne_SetsByte36AndLeavesTheRestZero()
    {
        // Given: the example given by VRChat's specification.
        var payload = new byte[VrchatFaceOsc.PayloadLength];

        // When
        VrchatFaceOsc.EncodePayload(ExpressionFrame.Create([new(UnifiedExpression.JawOpen, 1f)]), payload);

        // Then
        Assert.Equal(255, payload[36]);
        Assert.Equal(101, payload.Count(value => value == 0));
    }

    [Fact]
    public void KeyAt_EveryOffset_FollowsVrchatKeyOrder()
    {
        // When / Then
        Assert.Equal(UnifiedExpression.EyeLookOutRight, VrchatFaceOsc.KeyAt(0));
        Assert.Equal(UnifiedExpression.CheekPuffRight, VrchatFaceOsc.KeyAt(32));
        Assert.Equal(UnifiedExpression.MouthClosed, VrchatFaceOsc.KeyAt(43));
        Assert.Equal(UnifiedExpression.TongueUp, VrchatFaceOsc.KeyAt(87));
        Assert.Equal(UnifiedExpression.TongueDown, VrchatFaceOsc.KeyAt(88));
        Assert.Equal(UnifiedExpression.NeckFlexLeft, VrchatFaceOsc.KeyAt(101));
        Assert.Equal(102, Enumerable.Range(0, 102).Select(VrchatFaceOsc.KeyAt).Distinct().Count());
    }

    [Fact]
    public void EncodePayload_SomeKeysProvided_WritesEachOnItsOffsetAndZeroElsewhere()
    {
        // Given
        var frame = ExpressionFrame.Create([new(UnifiedExpression.MouthCornerPullLeft, .5f), new(UnifiedExpression.TongueOut, 0f)]);
        var payload = new byte[VrchatFaceOsc.PayloadLength];

        // When
        VrchatFaceOsc.EncodePayload(frame, payload);

        // Then
        Assert.Equal(128, payload[71]);
        Assert.Equal(0, payload[86]);
        Assert.Equal(1, payload.Count(value => value != 0));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    [InlineData(.5f, 128)]
    [InlineData(.498f, 127)]
    [InlineData(-1f, 0)]
    [InlineData(2f, 255)]
    public void Quantize_Value_RoundsToNearestByte(float value, byte expected)
    {
        // When
        var quantized = VrchatFaceOsc.Quantize(value);

        // Then
        Assert.Equal(expected, quantized);
    }

    [Fact]
    public void Quantize_NaN_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentOutOfRangeException>(() => VrchatFaceOsc.Quantize(float.NaN));
    }

    [Fact]
    public void WritePacket_Payload_WritesAddressBlobTagAndPaddedBlob()
    {
        // Given
        var payload = Enumerable.Range(1, 102).Select(value => (byte)value).ToArray();
        var packet = new byte[VrchatFaceOsc.PacketLength];

        // When
        VrchatFaceOsc.WritePacket(payload, packet);

        // Then
        byte[] expected =
        [
            .. Encoding.ASCII.GetBytes("/tracking/face/v1"), 0, 0, 0,
            (byte)',', (byte)'b', 0, 0,
            0, 0, 0, 102,
            .. payload, 0, 0
        ];
        Assert.Equal(132, expected.Length);
        Assert.Equal(expected, packet);
        Assert.Equal(payload, ResultAssert.Ok(VrchatFaceOsc.ReadPayload(packet)));
        packet[^1] = 1;
        Assert.Equal("packet", ResultAssert.Err(VrchatFaceOsc.ReadPayload(packet)).Name);
        Assert.Throws<ArgumentException>(() => VrchatFaceOsc.WritePacket(new byte[101], new byte[132]));
    }

    [Fact]
    public void Publish_FramesWithAndWithoutExpressions_SendsOnlyExpressionsAndCountsSuccessfulSends()
    {
        // Given
        var transport = new RecordingTransport();
        var output = new VrchatFaceOutput(transport);

        // When / Then: a frame without expressions sends nothing.
        Assert.True(output.Publish(Frame(null)) is Ok<Unit>);
        Assert.Empty(transport.Packets);

        // When / Then: a frame with expressions is sent and counted.
        Assert.True(output.Publish(Frame(ExpressionFrame.Create([new(UnifiedExpression.JawOpen, .2f)]))) is Ok<Unit>);
        Assert.Equal(51, ResultAssert.Ok(VrchatFaceOsc.ReadPayload(Assert.Single(transport.Packets)))[36]);
        Assert.Equal(1, output.Sent);

        // When / Then: a failed send is returned and not counted.
        transport.Failure = new OutputFailed("closed", 1);
        Assert.True(output.Send(ExpressionFrame.Empty) is Err<OutputFailed>(OutputFailed("closed", 1)));
        Assert.Equal(1, output.Sent);
    }

    [Fact]
    public async Task Open_SendThroughUdp_DeliversPacketAndReleasesSocket()
    {
        // Given
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)listener.Client.LocalEndPoint!;
        UdpOscTransport? opened = null;
        var program = UdpOscTransport.Open<PreviewEnvironment>(endpoint).Use(transport => Effect<PreviewEnvironment>.Sync(() =>
        {
            opened = transport;
            return new VrchatFaceOutput(transport).Send(ExpressionFrame.Create([new(UnifiedExpression.JawOpen, 1f)]));
        }));

        // When
        var exit = await program.RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Ok<Result<Unit, OutputFailed>>(Ok<Unit>));
        var received = await listener.ReceiveAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(255, ResultAssert.Ok(VrchatFaceOsc.ReadPayload(received.Buffer))[36]);
        Assert.Throws<ObjectDisposedException>(() => opened!.Send(new byte[VrchatFaceOsc.PacketLength]));
    }

    [Fact]
    public async Task Send_PortNobodyListensOn_KeepsSucceeding()
    {
        // Given: VRChat may be closed while Mouthtrap runs; UDP sends must not start failing after a port-unreachable reply.
        IPEndPoint closed;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) closed = (IPEndPoint)probe.Client.LocalEndPoint!;
        var program = Scope<TrackingError, PreviewEnvironment>.Gen(async scope =>
        {
            var transport = await scope.Acquire(UdpOscTransport.Open<PreviewEnvironment>(closed)
                .MapErr<TrackingError>(error => new TrackingOutputFailed(error)));
            var clock = scope.Env.Resolve<TimeProvider>();
            var output = new VrchatFaceOutput(transport);
            for (var i = 0; i < 20; i++)
            {
                // Each pause lets a port-unreachable reply arrive before the next send.
                if (output.Send(ExpressionFrame.Empty) is Err<OutputFailed>(var failure))
                    throw scope.Fail(new TrackingOutputFailed(failure));
                await Task.Delay(TimeSpan.FromMilliseconds(5), clock, scope.CancellationToken);
            }
            return output.Sent;
        });

        // When
        var exit = await program.RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Ok<long>(20), exit.Value?.ToString());
    }

    private static TrackingFrame Frame(ExpressionFrame? expressions) =>
        new(expressions is null ? null : new(new MouthEstimate(0f), expressions, null),
            new(new([0, 0, 0], 1, 1, 3), 0, new(PreprocessSettings.Default, 0), [0, 0, 0], 1, 1, 0, null, 0, 1, default));

    private sealed class RecordingTransport : IOscTransport
    {
        public List<byte[]> Packets { get; } = [];
        public OutputFailed? Failure { get; set; }

        public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
        {
            if (Failure is { } failure) return new Err<OutputFailed>(failure);
            Packets.Add(packet.ToArray());
            return new Ok<Unit>(Unit.Value);
        }
    }
}
