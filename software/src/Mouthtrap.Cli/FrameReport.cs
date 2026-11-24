using System.Collections.Immutable;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Cli;

// JSON Lines contract of a "frame" record. Expressions lists only the provided
// Unified Expressions keys, by their VRChat names.
public sealed record FrameReport(int Width, int Height, long Processed, long Received, long Replaced,
    float? RawJawOpen, float? JawOpen, double? InferenceMilliseconds, double FrameAgeMilliseconds,
    CalibrationSnapshot? Calibration = null, ImmutableArray<MouthExpression>? AdditionalExpressions = null,
    IReadOnlyDictionary<string, float>? Expressions = null, long Undecodable = 0)
{
    public static FrameReport From(TrackingFrame frame)
    {
        var (result, diagnostics) = (frame.Result, frame.Diagnostics);
        return new(diagnostics.Source.Width, diagnostics.Source.Height, diagnostics.Processed, diagnostics.Statistics.Received,
            diagnostics.Statistics.Replaced, result?.Estimate.RawJawOpen, result?.Estimate.JawOpen, diagnostics.InferenceMilliseconds,
            diagnostics.FrameAgeMilliseconds, result?.Calibration, result?.Estimate.AdditionalExpressions,
            result?.Expressions.Provided.ToDictionary(item => item.Key.ToString(), item => item.Value), diagnostics.Undecodable);
    }
}

internal sealed class ReportSink(Action<FrameReport> report) : ITrackingSink
{
    public Result<Unit, OutputFailed> Publish(TrackingFrame frame)
    {
        report(FrameReport.From(frame));
        return new Ok<Unit>(Unit.Value);
    }
}
