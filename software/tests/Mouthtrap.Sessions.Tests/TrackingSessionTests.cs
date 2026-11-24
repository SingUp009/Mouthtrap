using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Outputs;
using Mouthtrap.Sessions;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Sessions.Tests;

public sealed class TrackingSessionTests
{
    private static readonly Rgb24FrameImage Pixel = new([10, 20, 30], 1, 1, 3);

    [Fact]
    public void Create_OscWithoutModel_FailsBeforeOpeningTheTransport()
    {
        // Given
        var transport = new RecordingTransport();
        var request = new SessionRequest(new OneFrameSource(Pixel)) { Osc = transport.Resource() };

        // When
        var created = TrackingSession.Create(request);

        // Then
        Assert.True(created is Err<SessionSetupError>(OscRequiresModel));
        Assert.Empty(transport.Events);
    }

    [Fact]
    public void Create_ValidRequest_StartsWithTheRequestedPreprocessing()
    {
        // Given
        var preprocessing = new PreprocessSettings(CropRegion.FullFrame, QuarterTurn.Clockwise90, true);

        // When
        var session = ResultAssert.Ok(TrackingSession.Create(new(new OneFrameSource(Pixel)) { Preprocessing = preprocessing }));

        // Then
        Assert.Equal(preprocessing, session.Control.Current.Preprocessing);
        Assert.False(session.UsesModel);
        Assert.False(session.UsesOsc);
    }

    [Fact]
    public async Task Run_NoModel_PublishesTheFrameToTheHostSinkAndCountsIt()
    {
        // Given
        var session = ResultAssert.Ok(TrackingSession.Create(new(new OneFrameSource(Pixel))));
        var sink = new RecordingSink();

        // When
        var exit = await session.Run(sink).RunExitAsync(new(TimeProvider.System), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Ok<Unit>);
        var frame = Assert.Single(sink.Frames);
        Assert.Same(Pixel, frame.Diagnostics.Source);
        Assert.Null(frame.Result);
        Assert.Equal(1, session.Frames.Statistics.Received);
    }

    [Fact]
    public async Task Run_ModelFileMissing_FailsWithModelErrorAndSendsZerosBeforeReleasingOsc()
    {
        // Given
        var transport = new RecordingTransport();
        var source = new OneFrameSource(Pixel);
        var missing = Path.Combine(Path.GetTempPath(), $"mouthtrap-missing-{Guid.NewGuid():N}.json");
        var session = ResultAssert.Ok(TrackingSession.Create(new(source) { Model = missing, Osc = transport.Resource() }));

        // When
        var exit = await session.Run(new RecordingSink())
            .RunExitAsync(new(TimeProvider.System), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<TrackingError>(TrackingModelFailed(ModelFileMissing)));
        Assert.True(session.UsesModel);
        Assert.False(source.Started);
        Assert.Equal(["open", "send zeros", "release"], transport.Events);
        Assert.Equal(1, session.OscSent);
    }

    // Publishes one frame and ends, as a still image would without waiting.
    private sealed class OneFrameSource(FrameImage image) : IFrameSource
    {
        public bool Started { get; private set; }

        public Effect<Unit, FrameSourceError, TrackingEnvironment> Run(IFrameWriter frames) =>
            Scope<FrameSourceError, TrackingEnvironment>.Gen(async scope =>
            {
                Started = true;
                await Task.Yield();
                frames.Publish(image, scope.Env.Resolve<TimeProvider>().GetTimestamp());
                return Unit.Value;
            });
    }

    private sealed class RecordingSink : ITrackingSink
    {
        public List<TrackingFrame> Frames { get; } = [];

        public Result<Unit, OutputFailed> Publish(TrackingFrame frame)
        {
            Frames.Add(frame);
            return new Ok<Unit>(Unit.Value);
        }
    }

    private sealed class RecordingTransport : IOscTransport
    {
        public List<string> Events { get; } = [];

        public Resource<IOscTransport, OutputFailed, TrackingEnvironment> Resource() =>
            Moderato.Effects.Resource.Make(Effect<TrackingEnvironment>.Sync<IOscTransport>(() => { Events.Add("open"); return this; }),
                (_, _) => Effect<TrackingEnvironment>.Sync(() => { Events.Add("release"); return Unit.Value; }));

        public Result<Unit, OutputFailed> Send(ReadOnlySpan<byte> packet)
        {
            var payload = ResultAssert.Ok(VrchatFaceOsc.ReadPayload(packet));
            Events.Add(payload.All(value => value == 0) ? "send zeros" : "send");
            return new Ok<Unit>(Unit.Value);
        }
    }
}
