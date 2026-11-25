using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Mouthtrap.Inputs;

namespace Mouthtrap.Inputs.Camera.Tests;

public sealed class UsbCameraLocatorTests
{
    private const string Name = "UVC CAM1";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WaitForAsync_CameraAlreadyListed_ReturnsTrueAtOnce()
    {
        // Given
        var cameras = new Cameras([["Integrated Camera", Name]]);

        // When
        var found = await new UsbCameraLocator(cameras.List).WaitForAsync(Name, TimeSpan.FromSeconds(30), new FakeTimeProvider(),
            TestContext.Current.CancellationToken);

        // Then
        Assert.True(found);
        Assert.Equal(1, cameras.Listings);
    }

    [Fact]
    public async Task WaitForAsync_CameraAppearsAfterAFailedAndAnEmptyListing_ReturnsTrue()
    {
        // Given Windows failing once while the board re-enumerates, then listing nothing, then the camera
        var cameras = new Cameras([null, [], [Name]]);
        var clock = new TimerSignalingTimeProvider();

        // When
        var wait = new UsbCameraLocator(cameras.List).WaitForAsync(Name, TimeSpan.FromSeconds(30), clock, TestContext.Current.CancellationToken).AsTask();
        for (var listing = 0; listing < 2; listing++)
        {
            await Next(cameras.Listed);
            await Next(clock.TimersCreated);
            clock.Advance(UsbCameraLocator.Interval);
        }
        var found = await wait.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(found);
        Assert.Equal(3, cameras.Listings);
    }

    [Fact]
    public async Task WaitForAsync_CameraNeverListed_ReturnsFalseAfterTheTimeout()
    {
        // Given
        var cameras = new Cameras([[]]);
        var clock = new TimerSignalingTimeProvider();

        // When the camera stays missing for three seconds of a three-second timeout
        var wait = new UsbCameraLocator(cameras.List).WaitForAsync(Name, TimeSpan.FromSeconds(3), clock, TestContext.Current.CancellationToken).AsTask();
        while (!wait.IsCompleted)
        {
            await Next(cameras.Listed);
            if (wait.IsCompleted) break;
            await Next(clock.TimersCreated);
            clock.Advance(UsbCameraLocator.Interval);
        }
        var found = await wait.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.False(found);
        Assert.Equal(4, cameras.Listings);
    }

    [Fact]
    public async Task WaitForAsync_OtherNameDiffersOnlyInCase_IsNotTheCamera()
    {
        // Given
        var cameras = new Cameras([["uvc cam1"]]);

        // When
        var found = await new UsbCameraLocator(cameras.List).WaitForAsync(Name, TimeSpan.Zero, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.False(found);
    }

    [Fact]
    public async Task WaitForAsync_CallerCancels_ThrowsOperationCanceled()
    {
        // Given
        var cameras = new Cameras([[]]);
        var clock = new FakeTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // When
        var wait = new UsbCameraLocator(cameras.List).WaitForAsync(Name, TimeSpan.FromSeconds(30), clock, cancellation.Token).AsTask();
        await Next(cameras.Listed);
        await cancellation.CancelAsync();

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Bound, TestContext.Current.CancellationToken));
    }

    private static async Task Next(Channel<int> signals) =>
        await signals.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);

    // A FakeTimeProvider that signals each timer the code under test creates, so that a test advances time only once
    // the wait it wants to end exists.
    private sealed class TimerSignalingTimeProvider : FakeTimeProvider
    {
        public Channel<int> TimersCreated { get; } = Channel.CreateUnbounded<int>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TimersCreated.Writer.TryWrite(0);
            return timer;
        }
    }

    // The cameras Windows lists on each call, the last one repeating; null is a listing that failed. Listed signals each
    // listing.
    private sealed class Cameras(string[]?[] listings)
    {
        public Channel<int> Listed { get; } = Channel.CreateUnbounded<int>();
        public int Listings { get; private set; }

        public ValueTask<string[]?> List(CancellationToken cancellationToken)
        {
            var names = listings[Math.Min(Listings, listings.Length - 1)];
            Listings++;
            Listed.Writer.TryWrite(Listings);
            return ValueTask.FromResult(names);
        }
    }
}
