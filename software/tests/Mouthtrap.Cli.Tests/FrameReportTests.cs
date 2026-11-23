using System.Collections.Immutable;
using Mouthtrap.Cli;
using Mouthtrap.Core;
using Xunit;

namespace Mouthtrap.Cli.Tests;

public sealed class FrameReportTests
{
    private static readonly Rgb24FrameImage Source = new(new byte[640 * 480 * 3], 640, 480, 640 * 3);

    [Fact]
    public void From_ModeledFrame_ReportsSourceSizeCountersRawAndClampedValues()
    {
        // Given
        var expressions = ImmutableArray.Create(new MouthExpression("MouthPucker", 1.5f));
        var calibration = new CalibrationSnapshot(true, .5f, .1f, .9f);

        // When
        var report = FrameReport.From(Frame(new MouthEstimate(1.25f, expressions), 6.5, calibration));

        // Then
        Assert.Equal(new FrameReport(640, 480, 7, 9, 2, 1.25f, 1f, 6.5, 12.5, calibration, expressions), report);
    }

    [Fact]
    public void From_FrameWithExpressions_ListsOnlyProvidedKeysByVrchatName()
    {
        // Given
        var expressions = ExpressionFrame.Create([new(UnifiedExpression.JawOpen, .5f), new(UnifiedExpression.MouthCornerPullLeft, .25f)]);

        // When
        var report = FrameReport.From(Frame(new MouthEstimate(.5f), 6.5, null) with { Expressions = expressions });

        // Then
        Assert.Equal(new Dictionary<string, float> { ["JawOpen"] = .5f, ["MouthCornerPullLeft"] = .25f }, report.Expressions);
    }

    [Fact]
    public void From_FrameWithoutModel_ReportsNoEstimate()
    {
        // When
        var report = FrameReport.From(Frame(null, null, null));

        // Then
        Assert.Equal((640, 480, 7L), (report.Width, report.Height, report.Processed));
        Assert.Null(report.RawJawOpen);
        Assert.Null(report.JawOpen);
        Assert.Null(report.InferenceMilliseconds);
        Assert.Null(report.Calibration);
        Assert.Null(report.AdditionalExpressions);
        Assert.Null(report.Expressions);
    }

    private static TrackingFrame Frame(MouthEstimate? estimate, double? inferenceMs, CalibrationSnapshot? calibration) =>
        new(Source, 0, new(PreprocessSettings.Default, 0), new byte[3], 1, 1, .5, estimate, inferenceMs, calibration,
            12.5, 7, new FrameStatistics(9, 2, 0));
}
