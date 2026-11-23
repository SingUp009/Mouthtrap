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
        CaptureDeviceDescriptor descriptor, VideoCharacteristics mode, LatestFrameBuffer frames)
        where R : IHas<TimeProvider> =>
        Scope<CameraSessionError, R>.Gen(async scope =>
        {
            if (mode.PixelFormat == PixelFormats.Unknown || !descriptor.Characteristics.Contains(mode))
                throw scope.Fail(new InvalidCameraMode());
            var clock = scope.Env.Resolve<TimeProvider>();

            var open = Effect<R>.TryAsync(
                async ct => await descriptor.OpenAsync(mode, buffer =>
                {
                    var receivedAt = clock.GetTimestamp();
                    try { frames.Publish(buffer.Buffer.CopyImage(), receivedAt); }
                    catch (Exception exception) { frames.Fail(exception); }
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

            var startedAt = clock.GetTimestamp();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            while (await timer.WaitForNextTickAsync(scope.CancellationToken).ConfigureAwait(false))
            {
                if (frames.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                var statistics = frames.Statistics;
                var lastArrival = statistics.Received == 0 ? startedAt : statistics.LastReceivedAt;
                if (clock.GetElapsedTime(lastArrival) >= TimeSpan.FromSeconds(5))
                    throw scope.Fail(new CameraFramesTimedOut());
            }
            return Unit.Value;
        });
}
