using FlashCap;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Inputs;

public sealed record CameraFrameSource(CaptureDeviceDescriptor Descriptor, VideoCharacteristics Mode) : IFrameSource
{
    public Effect<Unit, FrameSourceError, PreviewEnvironment> Run(IFrameWriter frames) =>
        CameraSession.Run<PreviewEnvironment>(Descriptor, Mode, frames).MapErr<FrameSourceError>(Classify).OnThreadPool();

    private static FrameSourceError Classify(CameraSessionError error) => error switch
    {
        InvalidCameraMode => new InputSettingsInvalid(CameraMessages.Get("ModeUnavailable")),
        CameraOperationFailed(var operation, var code, var message) =>
            new InputUnavailable(CameraMessages.Format(operation == CameraOperation.Initialize ? "InitializeFailed" : "StartFailed", message), code),
        CameraFramesTimedOut => new InputLost(CameraMessages.Get("FramesTimedOut"))
    };
}
