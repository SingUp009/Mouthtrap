namespace Mouthtrap.Firmware;

// SLIP framing as the ESP ROM loader uses it: each packet is wrapped in 0xC0, and 0xC0 and 0xDB inside it are
// escaped as 0xDB 0xDC and 0xDB 0xDD.
internal static class Slip
{
    public const byte End = 0xc0;
    public const byte Escape = 0xdb;
    public const byte EscapedEnd = 0xdc;
    public const byte EscapedEscape = 0xdd;

    public static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        var frame = new List<byte>(packet.Length + 2) { End };
        foreach (var value in packet)
        {
            switch (value)
            {
                case End: frame.Add(Escape); frame.Add(EscapedEnd); break;
                case Escape: frame.Add(Escape); frame.Add(EscapedEscape); break;
                default: frame.Add(value); break;
            }
        }
        frame.Add(End);
        return [.. frame];
    }
}

// Reads SLIP frames from a board. Bytes between frames, such as the boot messages the ROM prints, are skipped, and
// a frame with an invalid escape is dropped, as the ROM sends nothing that needs either.
internal sealed class SlipReader(IBoardLink link)
{
    private readonly byte[] buffer = new byte[4096];
    private int position;
    private int length;

    // The next frame. A link that ends raises EndOfStreamException; cancellation raises OperationCanceledException.
    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = new List<byte>();
        var inFrame = false;
        var escaped = false;
        var valid = true;
        while (true)
        {
            if (position == length)
            {
                length = await link.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                position = 0;
                if (length == 0) throw new EndOfStreamException("The board closed the connection.");
            }
            var value = buffer[position++];
            if (!inFrame)
            {
                inFrame = value == Slip.End;
                continue;
            }
            if (value == Slip.End)
            {
                // Two delimiters in a row: the second one starts the frame.
                if (frame.Count > 0 && valid && !escaped) return [.. frame];
                frame.Clear();
                escaped = false;
                valid = true;
                continue;
            }
            if (escaped)
            {
                escaped = false;
                if (value == Slip.EscapedEnd) frame.Add(Slip.End);
                else if (value == Slip.EscapedEscape) frame.Add(Slip.Escape);
                else valid = false;
            }
            else if (value == Slip.Escape) escaped = true;
            else frame.Add(value);
        }
    }

    public void Clear() => position = length = 0;
}
