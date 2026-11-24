using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Core;

// An output could not deliver frames, such as a socket that cannot be opened or a send that fails.
public sealed record OutputFailed(string Message, int? Code = null);

public sealed record TrackingInputFailed(FrameSourceError Error);
public sealed record TrackingModelFailed(ModelError Error);
public sealed record TrackingOutputFailed(OutputFailed Error);

[Error]
public partial union TrackingError(TrackingInputFailed, TrackingModelFailed, TrackingOutputFailed);

// What tracking made of one frame. Estimate holds the model's raw outputs; Expressions holds what is sent:
// calibrated, smoothed Unified Expressions (see ExpressionProcessor). Calibration is the JawOpen range before
// smoothing, present while adaptive calibration is on.
public sealed record TrackingResult(MouthEstimate Estimate, ExpressionFrame Expressions, CalibrationSnapshot? Calibration);

// How one frame went through the pipeline, for previews and reports. Source is shared with the input and Pixels
// (the preprocessed, packed RGB24) belongs to the receivers; neither may be mutated. InferenceMilliseconds is null
// without a model.
public sealed record FrameDiagnostics(Rgb24FrameImage Source, long ReceivedAt, TrackingControlState Settings,
    byte[] Pixels, int PixelWidth, int PixelHeight, double PreprocessMilliseconds, double? InferenceMilliseconds,
    double FrameAgeMilliseconds, long Processed, FrameStatistics Statistics);

// One processed frame. Result is null without a model. Outputs that send results read Result; Diagnostics is for
// hosts that show or report how the frame was processed.
public sealed record TrackingFrame(TrackingResult? Result, FrameDiagnostics Diagnostics);

public interface ITrackingSink
{
    // Called serially on the tracking thread; return quickly. An Err ends the session as TrackingOutputFailed,
    // and an exception is a defect.
    Result<Unit, OutputFailed> Publish(TrackingFrame frame);
}

// Publishes to each sink in order and stops at the first failure.
public sealed class CompositeTrackingSink(params ITrackingSink[] sinks) : ITrackingSink
{
    private readonly ITrackingSink[] sinks = [.. sinks];

    public Result<Unit, OutputFailed> Publish(TrackingFrame frame)
    {
        foreach (var sink in sinks)
            if (sink.Publish(frame) is Err<OutputFailed> failure) return failure;
        return new Ok<Unit>(Unit.Value);
    }
}

public static class TrackingPipeline
{
    // Preprocessing output size when no model is loaded (capture and preprocessing check).
    public const int UnmodeledOutputSize = TrackingStages.UnmodeledOutputSize;

    // Owns the model; runs the input and the decode → preprocess → infer → calibrate loop until either ends.
    public static Effect<Unit, TrackingError, PreviewEnvironment> Run(IFrameSource input, LatestFrameBuffer frames,
        IFrameDecoder decoder, Effect<IMouthEstimator, ModelError, PreviewEnvironment>? loadModel, TrackingControl control,
        ITrackingSink sink, ExpressionOptions? expressionOptions = null) =>
        Scope<TrackingError, PreviewEnvironment>.Gen(async scope =>
        {
            IMouthEstimator? estimator = null;
            if (loadModel is { } load)
            {
                var resource = Resource.Make(load.MapErr<TrackingError>(error => new TrackingModelFailed(error)),
                    (model, _) => Effect<PreviewEnvironment>.Sync(() => { model.Dispose(); return Unit.Value; }));
                estimator = await scope.Acquire(resource);
            }
            scope.CancellationToken.ThrowIfCancellationRequested();
            var sourceEnded = 0;
            var source = input.Run(frames).MapErr<TrackingError>(error => new TrackingInputFailed(error))
                .Map(value => { Volatile.Write(ref sourceEnded, 1); return value; });
            // The loop takes and decodes frames, waits, publishes and lifts each stage's error into TrackingError;
            // the selector and the stages decide what is processed and how.
            var consume = Scope<TrackingError, PreviewEnvironment>.Gen(async consumer =>
            {
                var cancellationToken = consumer.CancellationToken;
                var clock = consumer.Env.Resolve<TimeProvider>();
                var startedAt = clock.GetTimestamp();
                var stages = new TrackingStages(estimator, expressionOptions ?? ExpressionOptions.Default);
                var selector = new FrameSelector();
                long processed = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Observe EOF before taking the slot so a final publication cannot be missed.
                    var ended = Volatile.Read(ref sourceEnded) != 0;
                    var settings = control.Current;
                    Rgb24FrameImage? arrived = null;
                    long receivedAt = 0;
                    if (frames.Take() is { } frame)
                    {
                        arrived = consumer.Get(decoder.ToRgb24(frame.Image).MapErr(error => new TrackingInputFailed(error)));
                        receivedAt = frame.ReceivedAt;
                    }
                    if (selector.Select(arrived, receivedAt, settings) is not { } selected)
                    {
                        if (ended) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(5), clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var result = consumer.Get(stages.Process(selected, startedAt, clock, cancellationToken)
                        .MapErr(error => new TrackingModelFailed(error)));
                    var diagnostics = new FrameDiagnostics(selected.Image, selected.ReceivedAt, selected.Settings, result.Pixels,
                        result.PixelWidth, result.PixelHeight, result.PreprocessMilliseconds, result.InferenceMilliseconds,
                        clock.GetElapsedTime(selected.ReceivedAt).TotalMilliseconds, ++processed, frames.Statistics);
                    consumer.Get(sink.Publish(new(result.Result, diagnostics)).MapErr(error => new TrackingOutputFailed(error)));
                }
                return Unit.Value;
            }).OnThreadPool();
            await scope.Run(Effect.All(source, consume, Concurrency.Parallel));
            return Unit.Value;
        });
}
