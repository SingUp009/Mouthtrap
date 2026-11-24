using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Core.Tests;

public sealed class FramePreprocessorTests
{
    [Theory]
    [InlineData(QuarterTurn.None, false, new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(QuarterTurn.None, true, new byte[] { 3, 2, 1, 6, 5, 4 })]
    [InlineData(QuarterTurn.Clockwise90, false, new byte[] { 4, 1, 5, 2, 6, 3 })]
    [InlineData(QuarterTurn.Clockwise90, true, new byte[] { 1, 4, 2, 5, 3, 6 })]
    [InlineData(QuarterTurn.HalfTurn, false, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(QuarterTurn.HalfTurn, true, new byte[] { 4, 5, 6, 1, 2, 3 })]
    [InlineData(QuarterTurn.Clockwise270, false, new byte[] { 3, 6, 2, 5, 1, 4 })]
    [InlineData(QuarterTurn.Clockwise270, true, new byte[] { 6, 3, 5, 2, 4, 1 })]
    public void Process_RotationAndMirror_WritesPixelsInExpectedOrder(QuarterTurn rotation, bool mirror, byte[] expected)
    {
        // Given
        var source = Gray([1, 2, 3, 4, 5, 6]);
        var output = new byte[18];
        var swap = rotation is QuarterTurn.Clockwise90 or QuarterTurn.Clockwise270;

        // When
        FramePreprocessor.Process(source, 3, 2, 9, CropRegion.FullFrame, rotation, mirror, output, swap ? 2 : 3, swap ? 3 : 2);

        // Then
        Assert.Equal(Gray(expected), output);
        Assert.Equal(Gray([1, 2, 3, 4, 5, 6]), source);
    }

    [Fact]
    public void Process_Crop_UsesSourceCoordinatesBeforeRotationAndIgnoresRowPadding()
    {
        // Given
        byte[] source = [1, 1, 1, 2, 2, 2, 3, 3, 3, 99, 99, 99, 4, 4, 4, 5, 5, 5, 6, 6, 6];
        var output = new byte[12];

        // When
        FramePreprocessor.Process(source, 3, 2, 12, ResultAssert.Ok(CropRegion.Create(1.0 / 3, 0, 2.0 / 3, 1)), QuarterTurn.Clockwise90, false, output, 2, 2);

        // Then
        Assert.Equal(Gray([5, 2, 6, 3]), output);
    }

    [Fact]
    public void Process_Upscale_InterpolatesFromPixelCentersInRgbOrder()
    {
        // Given
        byte[] source = [0, 10, 100, 100, 110, 200];
        var output = new byte[9];

        // When
        FramePreprocessor.Process(source, 2, 1, 6, CropRegion.FullFrame, QuarterTurn.None, false, output, 3, 1);

        // Then
        Assert.Equal(new byte[] { 0, 10, 100, 50, 60, 150, 100, 110, 200 }, output);
    }

    [Fact]
    public void Process_DownscaleBothDimensions_AveragesFourCenterNeighbors()
    {
        // Given
        var output = new byte[3];

        // When
        FramePreprocessor.Process(Gray([0, 40, 80, 120]), 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, output, 1, 1);

        // Then
        Assert.Equal(Gray([60]), output);
    }

    [Fact]
    public void Process_OnePixelCrop_ReplicatesWithoutReadingPastLastRow()
    {
        // Given
        var output = new byte[12];

        // When
        FramePreprocessor.Process(Gray([1, 2, 3, 4]), 2, 2, 6, ResultAssert.Ok(CropRegion.Create(0.5, 0.5, 0.5, 0.5)), QuarterTurn.Clockwise270, true, output, 2, 2);

        // Then
        Assert.Equal(Gray([4, 4, 4, 4]), output);
    }

    [Fact]
    public void FromPercent_TypedPercents_IsTheSameAsFractions()
    {
        // When / Then
        Assert.Equal(ResultAssert.Ok(CropRegion.Create(.24, .25, .48, .58)), ResultAssert.Ok(CropRegion.FromPercent(24, 25, 48, 58)));
        Assert.Equal("crop", ResultAssert.Err(CropRegion.FromPercent(60, 0, 50, 100)).Name);
    }

    [Theory]
    [InlineData(-0.1, 0, 1, 1)]
    [InlineData(0, -0.1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(0.8, 0, 0.3, 1)]
    [InlineData(0, 0.8, 1, 0.3)]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    public void Create_InvalidRegion_FailsWithInvalidValue(double x, double y, double width, double height)
    {
        // When
        var crop = CropRegion.Create(x, y, width, height);

        // Then
        Assert.Equal("crop", ResultAssert.Err(crop).Name);
    }

    [Fact]
    public void ToPixels_FractionalEdges_CoverRegionInsideImage()
    {
        // When / Then
        Assert.Equal(new PixelRegion(0, 0, 3, 2), CropRegion.FullFrame.ToPixels(3, 2));
        Assert.Equal(new PixelRegion(1, 1, 2, 1), ResultAssert.Ok(CropRegion.Create(0.5, 0.5, 0.5, 0.5)).ToPixels(3, 2));
        Assert.Equal(new PixelRegion(2, 1, 1, 1), ResultAssert.Ok(CropRegion.Create(0.99, 0.99, 0.001, 0.001)).ToPixels(3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CropRegion.FullFrame.ToPixels(0, 1));
    }

    [Fact]
    public void Process_InvalidBufferDimensionRotationOrAliasing_Throws()
    {
        // Given
        var source = Gray([1, 2, 3, 4]);
        var output = new byte[12];

        // When / Then
        Assert.Throws<ArgumentException>(() => FramePreprocessor.Process(source, 2, 2, 5, CropRegion.FullFrame, QuarterTurn.None, false, output, 2, 2));
        Assert.Throws<ArgumentException>(() => FramePreprocessor.Process(source[..11], 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, output, 2, 2));
        Assert.Throws<ArgumentException>(() => FramePreprocessor.Process(source, 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, new byte[11], 2, 2));
        Assert.Throws<ArgumentException>(() => FramePreprocessor.Process(source, 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, source, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => FramePreprocessor.Process(source, 2, 2, 6, CropRegion.FullFrame, (QuarterTurn)4, false, output, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => FramePreprocessor.Process(source, 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, output, 0, 2));
        Assert.Throws<ArgumentException>(() => FramePreprocessor.Process(source, 2, 2, 6, CropRegion.FullFrame, QuarterTurn.None, false, output, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void Rgb24FrameImage_LayoutAndBuffer_ValidatesLayoutAndSharesLatestFrameBuffer()
    {
        // When / Then: a layout that does not fit its buffer is rejected.
        Assert.Throws<ArgumentException>(() => new Rgb24FrameImage(new byte[2], 1, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rgb24FrameImage([], 0, 1, 0));

        // Given
        var frames = new LatestFrameBuffer();
        frames.Publish(new Rgb24FrameImage(Gray([1]), 1, 1, 3), 1);
        frames.Publish(new Rgb24FrameImage(Gray([2]), 1, 1, 3), 2);

        // When
        var latest = Assert.IsType<PreviewFrame>(frames.Take());

        // Then
        Assert.Equal(2, latest.ReceivedAt);
        Assert.Equal(Gray([2]), Assert.IsType<Rgb24FrameImage>(latest.Image).Data);
        Assert.Equal(new FrameStatistics(2, 1), frames.Statistics);
        Assert.Null(frames.Take());
    }

    private static byte[] Gray(byte[] values) => values.SelectMany(value => new[] { value, value, value }).ToArray();
}
