using Moderato.Effects;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// What makes an ESP32-S3 board one that Mouthtrap's XIAO firmware can run on, and how to read a board without writing
// it. Nothing a board reports tells a XIAO ESP32S3 Sense from another board with the same chip and memory, so the
// report is for the user to confirm.
public static class XiaoBoard
{
    // The flash every Mouthtrap XIAO firmware is laid out for.
    public const long FlashBytes = 8 * 1024 * 1024;
    // The XIAO's ESP32-S3R8 has 8 MB of octal PSRAM in the chip, which the firmware's camera buffers need.
    public const long PsramBytes = 8 * 1024 * 1024;

    // Reads the board on port and says whether the firmware can run on it, writing nothing. The board stays in its ROM
    // loader until it is written or reset.
    public static Effect<DeviceReport, ProvisioningError, ProvisioningEnvironment> Inspect(string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        return Inspect(BoardLink.Open(port), PsramBytes);
    }

    internal static Effect<DeviceReport, ProvisioningError, ProvisioningEnvironment> Inspect(
        Resource<IBoardLink, FlashError, ProvisioningEnvironment> board, long? requiredPsram) =>
        BoardInstaller.Identify(board).MapErr<ProvisioningError>(Classify).Map(identified => Describe(identified, FlashBytes, requiredPsram));

    // What the user is shown, with what keeps the firmware from running on the board. requiredPsram null accepts any,
    // as for Espressif's QEMU, whose eFuses are blank.
    internal static DeviceReport Describe(Esp32S3Board board, long requiredFlash, long? requiredPsram)
    {
        List<DeviceProblem> problems = [];
        if (board.FlashBytes is not { } flash)
            problems.Add(new(DeviceProblemKind.FlashUnknown, $"The flash reports ID 0x{board.FlashId:x6}, whose size is not known."));
        else if (flash < requiredFlash)
            problems.Add(new(DeviceProblemKind.FlashTooSmall,
                $"The firmware needs {BoardInstaller.Size(requiredFlash)} of flash; the board has {BoardInstaller.Size(flash)}."));
        if (requiredPsram is { } psram && board.EmbeddedPsramBytes != psram)
            problems.Add(new(DeviceProblemKind.MemoryMissing,
                $"The firmware needs {BoardInstaller.Size(psram)} of PSRAM in the chip, as on the XIAO's ESP32-S3R8; the board has {(board.EmbeddedPsramBytes is null ? "none" : BoardInstaller.Size(board.EmbeddedPsramBytes))}."));
        return new(board.Mac, board.Chip, board.FlashBytes, board.EmbeddedPsramBytes, problems);
    }

    // ESP32-S3 flashing errors in the terms every device shares.
    internal static ProvisioningError Classify(FlashError error) => error switch
    {
        PortUnavailable(var message) => new DeviceUnavailable(message),
        BoardNotResponding(var message) => new DeviceNotResponding(message),
        WrongChip(var message) => new DeviceIncompatible(message),
        BoardUnsuitable(var message) => new DeviceIncompatible(message),
        NotConfirmed(var message) => new DeviceNotConfirmed(message),
        BoardRejected(var message, _, _) => new DeviceWriteFailed(message),
        ConnectionLost(var message) => new DeviceWriteFailed(message),
        VerificationFailed(var message) => new DeviceWriteFailed(message),
    };
}
