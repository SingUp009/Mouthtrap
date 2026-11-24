using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

public sealed record ImageFileSource(string Path) : IFrameSource
{
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(IFrameWriter frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            var image = scope.Get(MediaFiles.Open(Path).AndThen(Decode));
            scope.CancellationToken.ThrowIfCancellationRequested();
            var clock = scope.Env.Resolve<TimeProvider>();
            frames.Publish(image, clock.GetTimestamp());
            // A still image stays the input until the session is stopped.
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, scope.CancellationToken);
            return Unit.Value;
        }).OnThreadPool();

    // WIC reports data it cannot decode as NotSupportedException or FileFormatException while the decoder loads the
    // first frame; the file is read whole here, so later reads cannot fail. Anything else is a defect.
    private static Result<Rgb24FrameImage, FrameSourceError> Decode(FileStream file)
    {
        BitmapSource frame;
        using (file)
        {
            try { frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; }
            catch (Exception exception) when (exception is NotSupportedException or FileFormatException)
            {
                return new Err<FrameSourceError>(new InputDataInvalid(exception.Message));
            }
        }
        return MediaFrames.ToRgb24(frame).MapErr<FrameSourceError>(invalid => invalid);
    }
}

public sealed record VideoFileSource(string Path, Dispatcher Dispatcher) : IFrameSource
{
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(IFrameWriter frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            var fullPath = scope.Get(MediaFiles.Open(Path).Map(file =>
            {
                // Opening the file first reports a missing or unreadable file before playback starts.
                using (file) return file.Name;
            }));
            var clock = scope.Env.Resolve<TimeProvider>();
            var resource = Resource.Make(Effect<PreviewEnvironment>.Async(async ct =>
                await Dispatcher.InvokeAsync(() => new VideoPlayback(frames, clock), DispatcherPriority.Normal, ct)),
                (playback, _) => Effect<PreviewEnvironment>.Async(async ct =>
                {
                    await Dispatcher.InvokeAsync(playback.Dispose, DispatcherPriority.Normal, ct);
                    return Unit.Value;
                }));
            var session = await scope.Acquire(resource);
            scope.CancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() => session.Open(fullPath), DispatcherPriority.Normal, scope.CancellationToken);
            var error = await session.Completion.Task.WaitAsync(scope.CancellationToken);
            if (error is { } failure) throw scope.Fail(failure);
            return Unit.Value;
        });
}

internal static class MediaFiles
{
    // Resolves and opens a chosen file for reading. A path that is not a valid path is a settings error; a file that
    // is missing, locked or denied is unavailable. These are the exceptions Path.GetFullPath and File.OpenRead raise
    // for such paths; anything else is a defect.
    public static Result<FileStream, FrameSourceError> Open(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (ArgumentException exception) { return new Err<FrameSourceError>(new InputSettingsInvalid(exception.Message)); }
        try { return new Ok<FileStream>(File.OpenRead(fullPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Err<FrameSourceError>(new InputUnavailable(exception.Message));
        }
    }
}

internal static class MediaFrames
{
    // An image outside the size limit is an expected data error.
    public static Result<Rgb24FrameImage, InputDataInvalid> ToRgb24(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
            bitmap.PixelWidth > 8192 || bitmap.PixelHeight > 8192 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 16_777_216)
            return new Err<InputDataInvalid>(new(FileSourceMessages.Get("ImageTooLarge")));
        var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
        var stride = checked(rgb.PixelWidth * 3);
        var data = new byte[checked(stride * rgb.PixelHeight)];
        rgb.CopyPixels(data, stride, 0);
        return new Ok<Rgb24FrameImage>(new(data, rgb.PixelWidth, rgb.PixelHeight, stride));
    }
}

internal sealed class VideoPlayback : IDisposable
{
    private readonly IFrameWriter frames;
    private readonly TimeProvider clock;
    private readonly MediaPlayer player = new() { IsMuted = true, Volume = 0 };
    private readonly DispatcherTimer timer;
    private readonly DrawingVisual visual = new();
    private RenderTargetBitmap? target;
    private long openedAt;
    private TimeSpan lastPosition = TimeSpan.MinValue;
    public TaskCompletionSource<FrameSourceError?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VideoPlayback(IFrameWriter frames, TimeProvider clock)
    {
        this.frames = frames;
        this.clock = clock;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += Tick;
        player.MediaOpened += Opened;
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
    }

    // path is the full path of a file that was opened before; MediaPlayer reports problems with its content through
    // MediaFailed, so an exception here is a defect.
    public void Open(string path)
    {
        openedAt = clock.GetTimestamp();
        player.Open(new Uri(path, UriKind.Absolute));
        player.Play();
        timer.Start();
    }

    private void Opened(object? sender, EventArgs e)
    {
        try
        {
            var width = player.NaturalVideoWidth;
            var height = player.NaturalVideoHeight;
            if (!player.HasVideo || width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_777_216)
            {
                Completion.TrySetResult(new InputDataInvalid(FileSourceMessages.Get("VideoMissingOrTooLarge")));
                return;
            }
            target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            using var drawing = visual.RenderOpen();
            drawing.DrawVideo(player, new Rect(0, 0, width, height));
        }
        catch (Exception exception) { Completion.TrySetException(exception); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        try
        {
            if (Completion.Task.IsCompleted) return;
            if (target is null)
            {
                if (clock.GetElapsedTime(openedAt) >= TimeSpan.FromSeconds(10))
                    Completion.TrySetResult(new InputUnavailable(FileSourceMessages.Get("VideoOpenTimedOut")));
                return;
            }
            var position = player.Position;
            if (position == lastPosition) return;
            var receivedAt = clock.GetTimestamp();
            target.Clear();
            target.Render(visual);
            switch (MediaFrames.ToRgb24(target))
            {
                case Ok<Rgb24FrameImage>(var image): frames.Publish(image, receivedAt); break;
                case Err<InputDataInvalid>(var error): Completion.TrySetResult(error); return;
            }
            lastPosition = position;
        }
        catch (Exception exception) { Completion.TrySetException(exception); }
    }

    private void Ended(object? sender, EventArgs e) => Completion.TrySetResult(null);
    // The file was opened before playback, so a media failure is about its content. MediaPlayer reports an
    // unreadable file as "media file not found", so its exception type is not used for classification.
    private void Failed(object? sender, ExceptionEventArgs e) =>
        Completion.TrySetResult(new InputDataInvalid(e.ErrorException.Message));

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= Tick;
        player.MediaOpened -= Opened;
        player.MediaEnded -= Ended;
        player.MediaFailed -= Failed;
        player.Close();
    }
}
