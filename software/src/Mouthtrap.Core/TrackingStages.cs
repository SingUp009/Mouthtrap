using Moderato.Results;

namespace Mouthtrap.Core;

// What the stages made of one frame. Pixels (packed RGB24) belongs to the receivers. Estimate, Calibration and
// Expressions are null without a model.
public sealed record ProcessedFrame(byte[] Pixels, int PixelWidth, int PixelHeight, double PreprocessMilliseconds,
    MouthEstimate? Estimate, double? InferenceMilliseconds, CalibrationSnapshot? Calibration, ExpressionFrame? Expressions);

// One session's per-frame stages: preprocess → estimate → express. Preprocessing and expression processing are
// pure; estimation is the model port. The estimator is borrowed: the pipeline owns and disposes it after the stages
// are done. Owned by the single tracking loop, so calls are serial.
public sealed class TrackingStages
{
    // Preprocessing output size when no model is loaded (capture and preprocessing check).
    public const int UnmodeledOutputSize = 128;

    private readonly IMouthEstimator? estimator;
    private readonly ExpressionProcessor processor;

    // Options must have passed Validate; the host checks user input before it starts a session.
    public TrackingStages(IMouthEstimator? estimator, ExpressionOptions options)
    {
        processor = new(options);
        this.estimator = estimator;
        OutputWidth = estimator?.InputWidth ?? UnmodeledOutputSize;
        OutputHeight = estimator?.InputHeight ?? UnmodeledOutputSize;
    }

    public int OutputWidth { get; }
    public int OutputHeight { get; }

    // Runs every stage on one frame. Calibration and smoothing follow the time since sessionStartedAt (a timestamp of
    // clock) when the estimate is ready. An inference error is typed and cancellation stays an
    // OperationCanceledException.
    public Result<ProcessedFrame, ModelInferenceFailed> Process(FrameSelection frame, long sessionStartedAt, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (frame.SettingsChanged) processor.Reset();

        var preprocessAt = clock.GetTimestamp();
        var pixels = Preprocess(frame.Image, frame.Settings.Preprocessing);
        var preprocessMs = clock.GetElapsedTime(preprocessAt).TotalMilliseconds;
        if (estimator is null)
            return new Ok<ProcessedFrame>(new(pixels, OutputWidth, OutputHeight, preprocessMs, null, null, null, null));

        var inferenceAt = clock.GetTimestamp();
        return estimator.Estimate(pixels, cancellationToken).Map(estimate =>
        {
            var inferenceMs = clock.GetElapsedTime(inferenceAt).TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            var (expressions, calibration) = processor.Process(estimate, clock.GetElapsedTime(sessionStartedAt));
            return new ProcessedFrame(pixels, OutputWidth, OutputHeight, preprocessMs, estimate, inferenceMs, calibration,
                expressions);
        });
    }

    // Crop, rotate, mirror and scale to the model input as a new RGB24 buffer.
    public byte[] Preprocess(Rgb24FrameImage image, PreprocessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        var pixels = new byte[checked(OutputWidth * OutputHeight * 3)];
        FramePreprocessor.Process(image.Data, image.Width, image.Height, image.Stride, settings.Crop, settings.Rotation,
            settings.Mirror, pixels, OutputWidth, OutputHeight);
        return pixels;
    }
}
