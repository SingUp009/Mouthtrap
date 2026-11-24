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
public readonly record struct FrameStatistics(long Received, long Replaced);

// Keeps only the newest frame: a slow consumer skips frames instead of falling behind.
public sealed class LatestFrameBuffer : IFrameWriter
{
    private readonly object gate = new();
    private PreviewFrame? latest;
    private long received;
    private long replaced;

    // Raised after each publication, on the publishing thread, so a consumer can wake instead of polling.
    public event Action? Published;

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
        }
        Published?.Invoke();
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
        get { lock (gate) return new(received, replaced); }
    }
}
