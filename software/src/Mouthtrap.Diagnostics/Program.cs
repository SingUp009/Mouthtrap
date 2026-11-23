using System.Text;
using FlashCap;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Diagnostics;
using Mouthtrap.Inputs;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += onCancel;
try
{
    var exit = await CameraDiagnostics.Enumerate<CameraEnvironment>()
        .RunExitAsync(new CameraEnvironment(new CaptureDevices()), cancellation.Token);
    return exit switch
    {
        Ok<CaptureDeviceDescriptor[]>(var devices) => CameraReport.Write(Console.Out, devices),
        Err<CameraDiagnosticError>(UnsupportedPlatform) => Fail("This diagnostic requires Windows."),
        Err<CameraDiagnosticError>(CameraEnumerationFailed(var code, var message)) =>
            Fail($"Camera enumeration failed (0x{code:X8}): {message}"),
        Die(var exception) => Fail($"Unexpected failure: {exception}"),
        Interrupted => 130,
    };
}
finally
{
    Console.CancelKeyPress -= onCancel;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
