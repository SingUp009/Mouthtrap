using System.Net;
using System.Net.Sockets;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Outputs;

// Where encoded OSC packets go: UDP to VRChat, or the CLI's dry run.
public interface IOscTransport
{
    Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet);
}

// Sends each frame's Unified Expressions as one /tracking/face/v1 packet. Frames without expressions (no model)
// send nothing. Called serially, as ITrackingSink requires.
public sealed class VrchatFaceOutput(IOscTransport transport, OscPacketCount? count = null) : ITrackingSink
{
    private readonly byte[] payload = new byte[VrchatFaceOsc.PayloadLength];
    private readonly byte[] packet = new byte[VrchatFaceOsc.PacketLength];
    private readonly OscPacketCount count = count ?? new();

    public long Sent => count.Sent;

    public Result<Unit, OutputFailed> Publish(TrackingFrame frame) =>
        frame.Result is { } result ? Send(result.Expressions) : new Ok<Unit>(Unit.Value);

    public Result<Unit, OutputFailed> Send(ExpressionFrame expressions)
    {
        VrchatFaceOsc.EncodePayload(expressions, payload);
        VrchatFaceOsc.WritePacket(payload, packet);
        var result = transport.Send(packet);
        if (result is Ok<Unit>) count.Add();
        return result;
    }
}

// An unconnected UDP socket owned by a Resource, so it is closed however the session ends.
public sealed class UdpOscTransport : IOscTransport, IDisposable
{
    private readonly Socket socket;
    private readonly IPEndPoint endpoint;

    private UdpOscTransport(IPEndPoint endpoint)
    {
        this.endpoint = endpoint;
        socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
    }

    public static Resource<UdpOscTransport, OutputFailed, R> Open<R>(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var open = Effect<R>.Try(() => new UdpOscTransport(endpoint),
            (SocketException exception) => new OutputFailed($"OSC socket: {exception.Message}", exception.ErrorCode));
        return Resource.Make(open, (transport, _) => Effect<R>.Sync(() => { transport.Dispose(); return Unit.Value; }));
    }

    public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
    {
        try
        {
            socket.SendTo(packet, SocketFlags.None, endpoint);
            return new Ok<Unit>(Unit.Value);
        }
        catch (SocketException exception)
        {
            return new Err<OutputFailed>(new($"OSC send to {endpoint}: {exception.Message}", exception.ErrorCode));
        }
    }

    public void Dispose() => socket.Dispose();
}
