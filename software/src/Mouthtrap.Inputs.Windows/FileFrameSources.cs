using System.IO;
using System.Runtime.ExceptionServices;
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
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(LatestFrameBuffer frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            var image = scope.Get(Decode(Path));
            scope.CancellationToken.ThrowIfCancellationRequested();
            var clock = scope.Env.Resolve<TimeProvider>();
            frames.Publish(image, clock.GetTimestamp());
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            while (await timer.WaitForNextTickAsync(scope.CancellationToken))
                if (frames.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            return Unit.Value;
        }).OnThreadPool();

    // Opening and decoding the file; only the failures MediaFrames.Classify recognizes are input errors.
    private static Result<Rgb24FrameImage, FrameSourceError> Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(System.IO.Path.GetFullPath(path));
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return MediaFrames.ToRgb24(decoder.Frames[0]).MapErr<FrameSourceError>(invalid => invalid);
        }
        catch (Exception exception) when (MediaFrames.Classify(exception) is { } error)
        {
            return new Err<FrameSourceError>(error);
        }
    }
}

public sealed record VideoFileSource(string Path, Dispatcher Dispatcher) : IFrameSource
{
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(LatestFrameBuffer frames) =>
        Scope<FrameSourceError, PreviewEnvironment>.Gen(async scope =>
        {
            scope.CancellationToken.ThrowIfCancellationRequested();
            var fullPath = scope.Get(Open(Path));
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

    // Opening the file first reports a missing or unreadable file before playback starts.
    private static Result<string, FrameSourceError> Open(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            using var file = File.OpenRead(fullPath);
            return new Ok<string>(fullPath);
        }
        catch (Exception exception) when (MediaFrames.Classify(exception) is { } error)
        {
            return new Err<FrameSourceError>(error);
        }
    }
}

internal static class MediaFrames
{
    // Null for exceptions that are not expected input failures, which stay defects.
    public static FrameSourceError? Classify(Exception exception) => exception switch
    {
        ArgumentException => new InputSettingsInvalid(exception.Message),
        InvalidDataException or FileFormatException or NotSupportedException => new InputDataInvalid(exception.Message),
        IOException or UnauthorizedAccessException => new InputUnavailable(exception.Message),
        _ => null
    };

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
    private readonly LatestFrameBuffer frames;
    private readonly TimeProvider clock;
    private readonly MediaPlayer player = new() { IsMuted = true, Volume = 0 };
    private readonly DispatcherTimer timer;
    private readonly DrawingVisual visual = new();
    private RenderTargetBitmap? target;
    private long openedAt;
    private TimeSpan lastPosition = TimeSpan.MinValue;
    public TaskCompletionSource<FrameSourceError?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VideoPlayback(LatestFrameBuffer frames, TimeProvider clock)
    {
        this.frames = frames;
        this.clock = clock;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += Tick;
        player.MediaOpened += Opened;
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
    }

    public void Open(string path)
    {
        try
        {
            openedAt = clock.GetTimestamp();
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
            timer.Start();
        }
        catch (Exception exception) when (MediaFrames.Classify(exception) is { } error)
        {
            Completion.TrySetResult(error);
        }
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
            if (frames.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
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
