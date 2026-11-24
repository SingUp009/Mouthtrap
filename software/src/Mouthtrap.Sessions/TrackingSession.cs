using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Inference;
using Mouthtrap.Inputs;
using Mouthtrap.Outputs;

namespace Mouthtrap.Sessions;

public sealed record OscRequiresModel;

[Error]
public partial union SessionSetupError(OscRequiresModel);

// What a host picked for one session. The host builds the input and the OSC transport; Model is the model
// definition JSON, or null to check capture and preprocessing only.
public sealed record SessionRequest(IFrameSource Input)
{
    public PreprocessSettings Preprocessing { get; init; } = PreprocessSettings.Default;
    public string? Model { get; init; }
    public ExpressionOptions Expressions { get; init; } = ExpressionOptions.Default;
    public Resource<IOscTransport, OutputFailed, TrackingEnvironment>? Osc { get; init; }
}

// One tracking session as the GUI and the CLI run it: the input, an optional model and optional VRChat output around
// TrackingPipeline. Frames and Control belong to this session, so run it once.
public sealed class TrackingSession
{
    private readonly SessionRequest request;
    private readonly OscPacketCount oscPackets = new();

    private TrackingSession(SessionRequest request)
    {
        this.request = request;
        Frames = new LatestFrameBuffer();
        Control = new TrackingControl(request.Preprocessing);
    }

    // What the host shows while the session runs: capture statistics, packets sent to VRChat, and settings changed
    // while it runs.
    public LatestFrameBuffer Frames { get; }
    public long OscSent => oscPackets.Sent;
    public TrackingControl Control { get; }
    public bool UsesModel => request.Model is not null;
    public bool UsesOsc => request.Osc is not null;

    // Checks the choices that depend on each other before anything is opened.
    public static Result<TrackingSession, SessionSetupError> Create(SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        if (request.Osc is not null && request.Model is null) return new Err<SessionSetupError>(new OscRequiresModel());
        return new Ok<TrackingSession>(new(request));
    }

    // Publishes every processed frame to sink, then to VRChat when OSC is used. VRChat output is a resource of the
    // session: it is opened before the model loads and gets zeros before it is released, however the session ends.
    public Effect<Unit, TrackingError, TrackingEnvironment> Run(ITrackingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Effect<Unit, TrackingError, TrackingEnvironment> Pipeline(ITrackingSink output) => TrackingPipeline.Run(request.Input,
            Frames, FrameImageDecoder.Instance, request.Model is { } model ? OnnxMouthEstimator.Load(model) : null, Control,
            output, request.Expressions);
        if (request.Osc is not { } transport) return Pipeline(sink);
        return Scope<TrackingError, TrackingEnvironment>.Gen(async scope =>
        {
            var osc = await scope.Acquire(VrchatOscSession.Output(transport, oscPackets)
                .MapErr<TrackingError>(error => new TrackingOutputFailed(error)));
            await scope.Run(Pipeline(new CompositeTrackingSink(sink, osc)));
            return Unit.Value;
        });
    }
}
