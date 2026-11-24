using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Outputs;

public static class VrchatOscSession
{
    // A UDP socket to VRChat's OSC input, as a transport resource for Run.
    public static Resource<IOscTransport, OutputFailed, PreviewEnvironment> Udp(OscEndpoint endpoint) =>
        UdpOscTransport.Open<PreviewEnvironment>(endpoint.Address).Map<IOscTransport>(transport => transport);

    // Runs a session with VRChat output added after the host's sink. The transport is a resource of the session,
    // and an all-zero packet is sent once when the session ends however it ends, before the transport is released,
    // so the avatar does not keep the last expression; that last send is best effort.
    public static Effect<Unit, TrackingError, PreviewEnvironment> Run(Resource<IOscTransport, OutputFailed, PreviewEnvironment> transport,
        ITrackingSink? sink, Action<VrchatFaceOutput> created,
        Func<ITrackingSink, Effect<Unit, TrackingError, PreviewEnvironment>> session)
    {
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(session);
        return Scope<TrackingError, PreviewEnvironment>.Gen(async scope =>
        {
            var opened = await scope.Acquire(transport.MapErr<TrackingError>(error => new TrackingOutputFailed(error)));
            var output = new VrchatFaceOutput(opened);
            created(output);
            scope.Defer(_ => Effect<PreviewEnvironment>.Sync(() => { output.Send(ExpressionFrame.Empty); return Unit.Value; }));
            await scope.Run(session(sink is null ? output : new CompositeTrackingSink(sink, output)));
            return Unit.Value;
        });
    }
}
