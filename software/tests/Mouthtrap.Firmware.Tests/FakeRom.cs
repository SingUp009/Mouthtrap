using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Mouthtrap.Firmware;

namespace Mouthtrap.Firmware.Tests;

// An ESP32-S3 ROM loader behind an IBoardLink: it decodes the SLIP packets written to it, keeps an 8 MB flash, and
// answers as the ROM does, with four status bytes. Each switch makes it misbehave in one way.
internal sealed class FakeRom : IBoardLink
{
    public const byte Sync = 0x08;
    public const byte FlashBegin = 0x02;
    public const byte FlashData = 0x03;
    public const byte WriteRegister = 0x09;
    public const byte ReadRegister = 0x0a;
    public const byte SetParameters = 0x0b;
    public const byte Attach = 0x0d;
    public const byte Md5 = 0x13;
    public const byte SecurityInfo = 0x14;

    private readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte> input = [];
    private byte[] pending = [];
    private uint writeOffset;
    private int rejectsLeft;

    public byte[] Flash { get; } = Enumerable.Repeat((byte)0xff, 8 * 1024 * 1024).ToArray();
    public List<byte> Commands { get; } = [];
    public List<(uint Address, uint Value, uint Mask)> RegisterWrites { get; } = [];
    public uint? FlashSize { get; private set; }
    public int ResetsToDownloadMode { get; private set; }
    public int Restarts { get; private set; }
    public bool Disposed { get; private set; }

    // Switches.
    public bool Silent { get; init; }
    public uint ChipId { get; init; } = 9;
    public uint ConsoleDevice { get; init; }
    public (byte Command, byte Reason, int Times)? Reject { get; init; }
    public bool CorruptFlash { get; init; }
    // After this many FLASH_DATA packets, the board answers nothing more (Silence) or closes the link (Close).
    public int? SilentAfterBlocks { get; init; }
    public int? CloseAfterBlocks { get; init; }
    public string[] LogAfterRestart { get; init; } = [];

    // One signal each time a read starts waiting for a board that has stopped answering.
    public Channel<int> SilentReads { get; } = Channel.CreateUnbounded<int>();
    public TaskCompletionSource Restarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int blocks;
    private bool silenced;

    public async ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (pending.Length == 0)
        {
            if (silenced) SilentReads.Writer.TryWrite(0);
            // A closed link reads 0 bytes, as IBoardLink requires.
            if (!await output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            pending = await output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        var count = Math.Min(buffer.Length, pending.Length);
        pending.AsSpan(0, count).CopyTo(buffer);
        pending = pending[count..];
        return count;
    }

    public ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        foreach (var value in data)
        {
            if (value == 0xc0)
            {
                if (input.Count > 0) Handle(Unescape(input));
                input.Clear();
            }
            else input.Add(value);
        }
        return ValueTask.CompletedTask;
    }

    public void DiscardInput() => pending = [];

    public ValueTask<bool> EnterDownloadModeAsync(int attempt, TimeProvider clock, CancellationToken cancellationToken)
    {
        ResetsToDownloadMode++;
        // The ROM prints its boot banner before it listens, as a board does after a reset.
        if (!Silent) output.Writer.TryWrite(Encoding.ASCII.GetBytes("ESP-ROM:esp32s3-20210327\r\nboot:0x0 (DOWNLOAD(USB/UART0))\r\nwaiting for download\r\n"));
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RestartAsync(TimeProvider clock, CancellationToken cancellationToken)
    {
        Restarts++;
        foreach (var line in LogAfterRestart) output.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\r\n"));
        Restarted.TrySetResult();
        return ValueTask.FromResult(true);
    }

    public void Emit(string text) => output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        Disposed = true;
        output.Writer.TryComplete();
    }

    private void Handle(byte[] packet)
    {
        if (Silent || silenced || packet.Length < 8 || packet[0] != 0) return;
        var command = packet[1];
        var data = packet.AsSpan(8, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))).ToArray();
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        Commands.Add(command);
        if (Reject is { } reject && reject.Command == command && rejectsLeft++ < reject.Times)
        {
            Answer(command, 0, [], status: 1, reason: reject.Reason);
            return;
        }
        switch (command)
        {
            case Sync:
                // The ROM answers SYNC eight times.
                for (var i = 0; i < 8; i++) Answer(command, 0x20120707, []);
                break;
            case SecurityInfo:
                var info = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), ChipId);
                Answer(command, 0, info);
                break;
            case ReadRegister:
                var address = BinaryPrimitives.ReadUInt32LittleEndian(data);
                Answer(command, address == 0x3FCEF14C ? ConsoleDevice : 0x1234, []);
                break;
            case WriteRegister:
                RegisterWrites.Add((BinaryPrimitives.ReadUInt32LittleEndian(data), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8))));
                Answer(command, 0, []);
                break;
            case SetParameters:
                FlashSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                Answer(command, 0, []);
                break;
            case FlashBegin:
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data);
                writeOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
                Flash.AsSpan((int)writeOffset, (int)size).Fill(0xff);
                Answer(command, 0, []);
                break;
            case FlashData:
                if (CloseAfterBlocks == blocks)
                {
                    output.Writer.TryComplete();
                    return;
                }
                if (SilentAfterBlocks == blocks)
                {
                    silenced = true;
                    return;
                }
                blocks++;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(data);
                var sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                var block = data.AsSpan(16, (int)length);
                if (RomLoader.Checksum(block) != checksum)
                {
                    Answer(command, 0, [], status: 1, reason: 0x07);
                    return;
                }
                block.CopyTo(Flash.AsSpan((int)(writeOffset + sequence * length)));
                Answer(command, 0, []);
                break;
            case Md5:
                var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                var digest = Convert.ToHexStringLower(MD5.HashData(Flash.AsSpan(start, count)));
                if (CorruptFlash) digest = new string('0', 32);
                Answer(command, 0, Encoding.ASCII.GetBytes(digest));
                break;
            default:
                Answer(command, 0, []);
                break;
        }
    }

    private void Answer(byte command, uint value, byte[] data, byte status = 0, byte reason = 0)
    {
        var packet = new byte[8 + data.Length + 4];
        packet[0] = 1;
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(data.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), value);
        data.CopyTo(packet, 8);
        packet[8 + data.Length] = status;
        packet[9 + data.Length] = reason;
        output.Writer.TryWrite(Slip.Encode(packet));
    }

    private static byte[] Unescape(List<byte> frame)
    {
        var packet = new List<byte>();
        for (var i = 0; i < frame.Count; i++)
        {
            if (frame[i] == 0xdb) packet.Add(frame[++i] == 0xdc ? (byte)0xc0 : (byte)0xdb);
            else packet.Add(frame[i]);
        }
        return [.. packet];
    }
}

// A FakeTimeProvider that reports when a timer with one due time is created, so a test advances past that timeout
// only once it exists.
internal sealed class DueTimeAwareTimeProvider(TimeSpan dueTime) : Microsoft.Extensions.Time.Testing.FakeTimeProvider
{
    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, due, period);
        if (due == dueTime) TimerCreated.TrySetResult();
        return timer;
    }
}

// A clock whose timers fire as soon as they are created, so every wait and timeout ends at once.
internal sealed class ExpiringTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        return timer;
    }
}
