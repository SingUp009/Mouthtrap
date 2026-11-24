using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Outputs;

// How many packets an output has sent, for the host to show while the output belongs to the session.
public sealed class OscPacketCount
{
    private long sent;

    public long Sent => Interlocked.Read(ref sent);

    internal void Add() => Interlocked.Increment(ref sent);
}

public static class VrchatOscSession
{
    // A UDP socket to VRChat's OSC input, as a transport resource for Output.
    public static Resource<IOscTransport, OutputFailed, PreviewEnvironment> Udp(OscEndpoint endpoint) =>
        UdpOscTransport.Open<PreviewEnvironment>(endpoint.Address).Map<IOscTransport>(transport => transport);

    // VRChat output over the transport. When it is released, however the session ended, an all-zero packet is sent
    // before the transport is released, so the avatar does not keep the last expression; that last send is best effort.
    // Count, when given, counts every packet this output sends.
    public static Resource<VrchatFaceOutput, OutputFailed, PreviewEnvironment> Output(
        Resource<IOscTransport, OutputFailed, PreviewEnvironment> transport, OscPacketCount? count = null) =>
        transport.AndThen<VrchatFaceOutput>(opened => Resource.Make(
            Effect<PreviewEnvironment>.Sync(() => new VrchatFaceOutput(opened, count)),
            (output, _) => Effect<PreviewEnvironment>.Sync(() => { output.Send(ExpressionFrame.Empty); return Unit.Value; })));
}
