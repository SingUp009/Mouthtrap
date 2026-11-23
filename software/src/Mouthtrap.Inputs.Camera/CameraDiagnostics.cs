using System.Runtime.InteropServices;
using FlashCap;
using Moderato.Effects;

namespace Mouthtrap.Inputs;

public sealed record UnsupportedPlatform;
public sealed record CameraEnumerationFailed(int Code, string Message);

[Error]
public partial union CameraDiagnosticError(UnsupportedPlatform, CameraEnumerationFailed);

[Moderato.Effects.Environment]
public sealed partial record CameraEnvironment(CaptureDevices Devices);

public static class CameraDiagnostics
{
    public static Effect<CaptureDeviceDescriptor[], CameraDiagnosticError, R> Enumerate<R>()
        where R : IHas<CaptureDevices> =>
        Scope<CameraDiagnosticError, R>.Gen(async scope =>
        {
            if (!OperatingSystem.IsWindows()) throw scope.Fail(new UnsupportedPlatform());
            scope.CancellationToken.ThrowIfCancellationRequested();
            var devices = scope.Env.Resolve<CaptureDevices>();
            var descriptors = await scope.Run(Effect<R>.Try(
                () => devices.EnumerateDescriptors().ToArray(),
                (COMException exception) => new CameraEnumerationFailed(exception.HResult, exception.Message)));
            scope.CancellationToken.ThrowIfCancellationRequested();
            return descriptors;
        });
}
