using Moderato.Effects;

namespace Mouthtrap.Firmware;

// Writing to a board fails in ways that are classified by what the user can do about them.
// The serial port cannot be opened: it is missing, busy in another program, or access is denied.
public sealed record PortUnavailable(string Message);
// The port opened but no ROM loader answered: the board is not in download mode or is not an ESP chip.
public sealed record BoardNotResponding(string Message);
// The board is not the chip the firmware is built for.
public sealed record WrongChip(string Message);
// The ROM loader refused a command. Status and Reason are the bytes it returned.
public sealed record BoardRejected(string Message, byte Status, byte Reason);
// The board answered at first and then stopped (unplugged, reset, or a broken cable).
public sealed record ConnectionLost(string Message);
// The flash does not hold what was written.
public sealed record VerificationFailed(string Message);

[Error]
public partial union FlashError(PortUnavailable, BoardNotResponding, WrongChip, BoardRejected, ConnectionLost, VerificationFailed);

// What writing firmware runs with: the clock for timeouts and the waits of the reset sequences.
[Moderato.Effects.Environment]
public sealed partial record FirmwareEnvironment(TimeProvider Clock);
