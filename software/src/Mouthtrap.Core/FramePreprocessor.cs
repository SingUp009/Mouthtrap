using Moderato.Results;

namespace Mouthtrap.Core;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height);

public sealed record CropRegion
{
    public static CropRegion FullFrame { get; } = new(0, 0, 1, 1);
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    private CropRegion(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    // A crop typed by the user or read from settings, in fractions of the frame. It must be a nonempty rectangle
    // inside [0, 1]; anything else is an expected InvalidValue.
    public static Result<CropRegion, InvalidValue> Create(double x, double y, double width, double height) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) &&
        x >= 0 && y >= 0 && width > 0 && height > 0 && x + width <= 1 && y + height <= 1
            ? new Ok<CropRegion>(new(x, y, width, height))
            : new Err<InvalidValue>(new("crop", "The crop must be a nonempty rectangle inside the frame."));

    public PixelRegion ToPixels(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var x = Math.Min(width - 1, (int)Math.Floor(X * width));
        var y = Math.Min(height - 1, (int)Math.Floor(Y * height));
        var right = Math.Min(width, (int)Math.Ceiling((X + Width) * width));
        var bottom = Math.Min(height, (int)Math.Ceiling((Y + Height) * height));
        return new(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}

public enum QuarterTurn { None, Clockwise90, HalfTurn, Clockwise270 }

public static class FramePreprocessor
{
    public static void Process(ReadOnlySpan<byte> source, int width, int height, int stride,
        CropRegion region, QuarterTurn rotation, bool mirror, Span<byte> destination, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        if (!Enum.IsDefined(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (stride < (long)width * 3 || source.Length < (long)(height - 1) * stride + (long)width * 3)
            throw new ArgumentException("RGB24 source or stride is too small.", nameof(source));
        if ((long)outputWidth * outputHeight > destination.Length / 3)
            throw new ArgumentException("RGB24 destination is too small.", nameof(destination));
        if (source.Overlaps(destination)) throw new ArgumentException("Source and destination must not overlap.", nameof(destination));

        var crop = region.ToPixels(width, height);
        var swap = rotation is QuarterTurn.Clockwise90 or QuarterTurn.Clockwise270;
        var rotatedWidth = swap ? crop.Height : crop.Width;
        var rotatedHeight = swap ? crop.Width : crop.Height;
        for (var dy = 0; dy < outputHeight; dy++)
        {
            var v = Math.Clamp((dy + 0.5) * rotatedHeight / outputHeight - 0.5, 0, rotatedHeight - 1);
            for (var dx = 0; dx < outputWidth; dx++)
            {
                var u = Math.Clamp((dx + 0.5) * rotatedWidth / outputWidth - 0.5, 0, rotatedWidth - 1);
                if (mirror) u = rotatedWidth - 1 - u;
                var (sx, sy) = rotation switch
                {
                    QuarterTurn.None => (u, v),
                    QuarterTurn.Clockwise90 => (v, crop.Height - 1 - u),
                    QuarterTurn.HalfTurn => (crop.Width - 1 - u, crop.Height - 1 - v),
                    QuarterTurn.Clockwise270 => (crop.Width - 1 - v, u),
                    _ => throw new ArgumentOutOfRangeException(nameof(rotation))
                };
                var x0 = (int)Math.Floor(sx);
                var y0 = (int)Math.Floor(sy);
                var x1 = Math.Min(x0 + 1, crop.Width - 1);
                var y1 = Math.Min(y0 + 1, crop.Height - 1);
                var wx = sx - x0;
                var wy = sy - y0;
                var p00 = (crop.Y + y0) * stride + (crop.X + x0) * 3;
                var p10 = (crop.Y + y0) * stride + (crop.X + x1) * 3;
                var p01 = (crop.Y + y1) * stride + (crop.X + x0) * 3;
                var p11 = (crop.Y + y1) * stride + (crop.X + x1) * 3;
                var target = (dy * outputWidth + dx) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source[p00 + channel] * (1 - wx) + source[p10 + channel] * wx;
                    var bottom = source[p01 + channel] * (1 - wx) + source[p11 + channel] * wx;
                    destination[target + channel] = (byte)(top * (1 - wy) + bottom * wy + 0.5);
                }
            }
        }
    }
}
