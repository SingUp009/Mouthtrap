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

// Source is shared with the input and Pixels (packed RGB24) belongs to the receivers; neither may be mutated.
// Estimate holds the model's raw outputs; Expressions holds what is sent: calibrated, smoothed Unified Expressions
// (see ExpressionProcessor). Calibration is the JawOpen range before smoothing.
public sealed record TrackingFrame(Rgb24FrameImage Source, long ReceivedAt, TrackingControlState Settings,
    byte[] Pixels, int PixelWidth, int PixelHeight, double PreprocessMilliseconds, MouthEstimate? Estimate,
    double? InferenceMilliseconds, CalibrationSnapshot? Calibration, double FrameAgeMilliseconds, long Processed,
    FrameStatistics Statistics, ExpressionFrame? Expressions = null);

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
    public const int UnmodeledOutputSize = 128;

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
            var consume = Scope<TrackingError, PreviewEnvironment>.Gen(async consumer =>
            {
                var cancellationToken = consumer.CancellationToken;
                var clock = consumer.Env.Resolve<TimeProvider>();
                var startedAt = clock.GetTimestamp();
                var options = expressionOptions ?? ExpressionOptions.Default;
                var processor = new ExpressionProcessor(options);
                var width = estimator?.InputWidth ?? UnmodeledOutputSize;
                var height = estimator?.InputHeight ?? UnmodeledOutputSize;
                Rgb24FrameImage? held = null;
                long heldReceivedAt = 0;
                int? revision = null;
                long processed = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Observe EOF before taking the slot so a final publication cannot be missed.
                    var ended = Volatile.Read(ref sourceEnded) != 0;
                    var settings = control.Current;
                    Rgb24FrameImage image;
                    if (frames.Take() is { } frame)
                    {
                        image = held = consumer.Get(decoder.ToRgb24(frame.Image).MapErr(error => new TrackingInputFailed(error)));
                        heldReceivedAt = frame.ReceivedAt;
                    }
                    // A settings change reprocesses the held frame, so a still image follows it as well.
                    else if (held is not null && settings.Revision != revision) image = held;
                    else if (ended) break;
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (settings.Revision != revision)
                    {
                        processor.Reset();
                        revision = settings.Revision;
                    }

                    var preprocessAt = clock.GetTimestamp();
                    var pixels = new byte[checked(width * height * 3)];
                    var preprocessing = settings.Preprocessing;
                    FramePreprocessor.Process(image.Data, image.Width, image.Height, image.Stride, preprocessing.Crop,
                        preprocessing.Rotation, preprocessing.Mirror, pixels, width, height);
                    var preprocessMs = clock.GetElapsedTime(preprocessAt).TotalMilliseconds;

                    MouthEstimate? estimate = null;
                    double? inferenceMs = null;
                    CalibrationSnapshot? calibrated = null;
                    ExpressionFrame? expressions = null;
                    if (estimator is not null)
                    {
                        var inferenceAt = clock.GetTimestamp();
                        estimate = estimator.Estimate(pixels, cancellationToken) switch
                        {
                            Ok<MouthEstimate>(var value) => value,
                            Err<ModelInferenceFailed>(var error) => throw consumer.Fail(new TrackingModelFailed(error))
                        };
                        inferenceMs = clock.GetElapsedTime(inferenceAt).TotalMilliseconds;
                        cancellationToken.ThrowIfCancellationRequested();
                        (expressions, calibrated) = processor.Process(estimate, clock.GetElapsedTime(startedAt));
                    }
                    var published = sink.Publish(new(image, heldReceivedAt, settings, pixels, width, height, preprocessMs,
                        estimate, inferenceMs, calibrated, clock.GetElapsedTime(heldReceivedAt).TotalMilliseconds,
                        ++processed, frames.Statistics, expressions));
                    if (published is Err<OutputFailed>(var failure)) throw consumer.Fail(new TrackingOutputFailed(failure));
                }
                return Unit.Value;
            }).OnThreadPool();
            await scope.Run(Effect.All(source, consume, Concurrency.Parallel));
            return Unit.Value;
        });
}
