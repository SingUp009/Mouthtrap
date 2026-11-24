using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using FlashCap;
using Moderato.Effects;
using Moderato.Results;

using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

public enum CameraOperation { Initialize, Start }
public sealed record CameraOperationFailed(CameraOperation Operation, int Code, string Message);
public sealed record InvalidCameraMode;
public sealed record CameraFramesTimedOut;

[Error]
public partial union CameraSessionError(CameraOperationFailed, InvalidCameraMode, CameraFramesTimedOut);

public static class CameraSession
{
    public static Effect<Unit, CameraSessionError, R> Run<R>(
        CaptureDeviceDescriptor descriptor, VideoCharacteristics mode, IFrameWriter frames)
        where R : IHas<TimeProvider> =>
        Scope<CameraSessionError, R>.Gen(async scope =>
        {
            if (mode.PixelFormat == PixelFormats.Unknown || !descriptor.Characteristics.Contains(mode))
                throw scope.Fail(new InvalidCameraMode());
            var clock = scope.Env.Resolve<TimeProvider>();
            var arrivals = new Arrivals(clock.GetTimestamp());

            var open = Effect<R>.TryAsync(
                async ct => await descriptor.OpenAsync(mode, buffer =>
                {
                    var receivedAt = clock.GetTimestamp();
                    // FlashCap calls this on its own thread, so a failure is kept for the watchdog below.
                    try
                    {
                        frames.Publish(new EncodedFrameImage(buffer.Buffer.CopyImage()), receivedAt);
                        arrivals.Arrived(receivedAt);
                    }
                    catch (Exception exception) { arrivals.Fail(exception); }
                }, ct).ConfigureAwait(false),
                (COMException exception) => new CameraOperationFailed(CameraOperation.Initialize, exception.HResult, exception.Message));

            var resource = Resource.Make(open, (device, _) => Effect<R>.Async(async ct =>
            {
                Exception? stopFailure = null;
                try { await device.StopAsync(ct).ConfigureAwait(false); }
                catch (Exception exception) { stopFailure = exception; }
                try { await device.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (stopFailure is not null)
                {
                    throw new AggregateException(stopFailure, exception);
                }
                if (stopFailure is not null) ExceptionDispatchInfo.Capture(stopFailure).Throw();
                return Unit.Value;
            }));

            var capture = await scope.Acquire(resource);
            scope.CancellationToken.ThrowIfCancellationRequested();
            await scope.Run(Effect<R>.TryAsync(async ct =>
            {
                await capture.StartAsync(ct).ConfigureAwait(false);
                return Unit.Value;
            }, (COMException exception) => new CameraOperationFailed(CameraOperation.Start, exception.HResult, exception.Message)));

            arrivals.Arrived(clock.GetTimestamp());
            // The watchdog checks for stalls every second and wakes at once when the frame callback fails.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            while (true)
            {
                var tick = timer.WaitForNextTickAsync(scope.CancellationToken).AsTask();
                if (await Task.WhenAny(tick, arrivals.Failed).ConfigureAwait(false) == arrivals.Failed)
                    await arrivals.Failed.ConfigureAwait(false);
                if (!await tick.ConfigureAwait(false)) break;
                if (clock.GetElapsedTime(arrivals.Last) >= TimeSpan.FromSeconds(5))
                    throw scope.Fail(new CameraFramesTimedOut());
            }
            return Unit.Value;
        });

    // Shared between FlashCap's frame thread and the watchdog: the last arrival (or the start) and the first failure
    // of the frame callback, which the watchdog rethrows as a defect.
    private sealed class Arrivals(long startedAt)
    {
        private readonly TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long last = startedAt;

        public long Last => Interlocked.Read(ref last);
        public Task Failed => failed.Task;

        public void Arrived(long receivedAt) => Interlocked.Exchange(ref last, receivedAt);
        public void Fail(Exception exception) => failed.TrySetException(exception);
    }
}
