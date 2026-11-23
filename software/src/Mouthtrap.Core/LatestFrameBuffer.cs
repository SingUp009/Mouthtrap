namespace Mouthtrap.Core;

public abstract record FrameImage;
public sealed record EncodedFrameImage(byte[] Data) : FrameImage;
public sealed record Rgb24FrameImage : FrameImage
{
    public byte[] Data { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public Rgb24FrameImage(byte[] data, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < (long)width * 3 || data.LongLength < (long)(height - 1) * stride + (long)width * 3)
            throw new ArgumentException("RGB24 buffer or stride is too small.", nameof(data));
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
    }
}

public sealed record PreviewFrame(FrameImage Image, long ReceivedAt);
public readonly record struct FrameStatistics(long Received, long Replaced, long LastReceivedAt);

public sealed class LatestFrameBuffer
{
    private readonly object gate = new();
    private PreviewFrame? latest;
    private long received;
    private long replaced;
    private long lastReceivedAt;
    private Exception? failure;

    public void Publish(byte[] ownedImage, long receivedAt)
    {
        ArgumentNullException.ThrowIfNull(ownedImage);
        Publish(new EncodedFrameImage(ownedImage), receivedAt);
    }

    // Publishing transfers ownership: the producer must not mutate the image afterwards.
    public void Publish(FrameImage ownedImage, long receivedAt)
    {
        ArgumentNullException.ThrowIfNull(ownedImage);
        var frame = new PreviewFrame(ownedImage, receivedAt);
        lock (gate)
        {
            if (latest is not null) replaced++;
            latest = frame;
            received++;
            lastReceivedAt = frame.ReceivedAt;
        }
    }

    public PreviewFrame? Take()
    {
        lock (gate)
        {
            var frame = latest;
            latest = null;
            return frame;
        }
    }

    public FrameStatistics Statistics
    {
        get { lock (gate) return new(received, replaced, lastReceivedAt); }
    }

    public void Fail(Exception exception)
    {
        lock (gate) failure ??= exception;
    }

    public Exception? Failure
    {
        get { lock (gate) return failure; }
    }
}
