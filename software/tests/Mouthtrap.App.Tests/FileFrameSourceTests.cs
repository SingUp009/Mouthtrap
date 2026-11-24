using System.IO;
using System.Resources;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Inputs;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.App.Tests;

public sealed class FileFrameSourceTests
{
    [Fact]
    public Task Run_StillImage_PublishesOwnedRgbOnceAndReleasesFileBeforeStopping() => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-{Guid.NewGuid():N}.png");
        byte[] expected = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null, expected, 6);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        using var cancellation = new CancellationTokenSource();
        var frames = new LatestFrameBuffer();
        IFrameSource source = new ImageFileSource(path);

        // When
        var run = source.Run(frames).RunExitAsync(new(TimeProvider.System), cancellation.Token).AsTask();
        try
        {
            // Then: LatestFrameBuffer has no arrival signal, so the test checks for the frame until it arrives.
            var deadline = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            while (frames.Statistics.Received == 0 && !run.IsCompleted && !deadline.IsCompleted)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            var frame = Assert.IsType<PreviewFrame>(frames.Take());
            var rgb = Assert.IsType<Rgb24FrameImage>(frame.Image);
            Assert.Equal((2, 2, 6), (rgb.Width, rgb.Height, rgb.Stride));
            Assert.Equal(expected, rgb.Data);
            Assert.Equal(1, frames.Statistics.Received);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            cancellation.Cancel();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            File.Delete(path);
            Assert.True(exit is Interrupted);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Run_MissingFile_FailsAsUnavailable(bool video) => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-missing-{Guid.NewGuid():N}.bin");
        IFrameSource source = video ? new VideoFileSource(path, Dispatcher.CurrentDispatcher) : new ImageFileSource(path);

        // When
        var exit = await source.Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputUnavailable));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Run_PathWithNullCharacter_FailsAsSettingsError(bool video) => OnSta(async () =>
    {
        // Given
        IFrameSource source = video ? new VideoFileSource("still\0.png", Dispatcher.CurrentDispatcher) : new ImageFileSource("still\0.png");

        // When
        var exit = await source.Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputSettingsInvalid));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Run_PathIsAFolder_FailsAsUnavailable(bool video) => OnSta(async () =>
    {
        // Given
        var folder = Path.GetTempPath();
        IFrameSource source = video ? new VideoFileSource(folder, Dispatcher.CurrentDispatcher) : new ImageFileSource(folder);

        // When
        var exit = await source.Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

        // Then
        Assert.True(exit is Err<FrameSourceError>(InputUnavailable));
    });

    [Fact]
    public Task Run_InvalidImage_FailsWithDataError() => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-invalid-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(path, "not an image");
        try
        {
            // When
            var exit = await new ImageFileSource(path).Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

            // Then
            Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public Task Run_OversizedImage_FailsWithDataError() => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-oversized-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8193, 1, 96, 96, PixelFormats.Gray8, null, new byte[8193], 8193)));
        await using (var file = File.Create(path)) encoder.Save(file);
        try
        {
            // When
            var exit = await new ImageFileSource(path).Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None);

            // Then
            Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void FileSourceMessages_EveryMessage_HasJapaneseTranslation()
    {
        // Given
        var resources = new ResourceManager("Mouthtrap.Inputs.FileSourceMessages", typeof(ImageFileSource).Assembly);

        // When / Then
        ResourceAssert.Translated(resources, "ja");
    }

    [Fact]
    public Task Run_InvalidVideo_FailsWithDataErrorAndReleasesPlayer() => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-invalid-{Guid.NewGuid():N}.avi");
        await File.WriteAllTextAsync(path, "not a video");
        try
        {
            var source = new VideoFileSource(path, Dispatcher.CurrentDispatcher);

            // When
            var exit = await source.Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Then
            Assert.True(exit is Err<FrameSourceError>(InputDataInvalid));
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally { File.Delete(path); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Run_AlreadyCancelled_DoesNotOpenFile(bool video) => OnSta(async () =>
    {
        // Given
        var path = Path.Combine(Path.GetTempPath(), $"mouthtrap-missing-{Guid.NewGuid():N}.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IFrameSource source = video ? new VideoFileSource(path, Dispatcher.CurrentDispatcher) : new ImageFileSource(path);

        // When
        var exit = await source.Run(new LatestFrameBuffer()).RunExitAsync(new(TimeProvider.System), cancellation.Token);

        // Then
        Assert.True(exit is Interrupted);
    });

    [Fact]
    public void ToRgb24_UndecodableJpeg_FailsWithDataError()
    {
        // Given
        var frame = new EncodedFrameImage([0xff, 0xd8, 1, 2, 3, 0xff, 0xd9]);

        // When
        var decoded = FrameImageDecoder.Instance.ToRgb24(frame);

        // Then
        Assert.False(string.IsNullOrEmpty(ResultAssert.Err(decoded).Message));
    }

    [Fact]
    public void ToRgb24_UnknownFrameImage_Throws()
    {
        // When / Then
        Assert.Throws<NotSupportedException>(() => FrameImageDecoder.Instance.ToRgb24(new OtherFrameImage()));
    }

    private sealed record OtherFrameImage : FrameImage;

    private static Task OnSta(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();
            async Task RunAsync()
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception exception) { finished.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}
