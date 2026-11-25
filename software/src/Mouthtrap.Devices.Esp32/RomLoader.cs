using System.Buffers.Binary;
using System.Security.Cryptography;
using Moderato.Effects;
using Moderato.Results;

namespace Mouthtrap.Devices;

// The ESP32-S3 ROM loader's serial protocol, as esptool 4.14 speaks it without its RAM stub: SLIP packets of a
// direction byte, a command, a length and a checksum, answered by the same command with a value, data and status
// bytes. Flash is written uncompressed in 1 KiB blocks, as esptool does with the ROM loader. Transport failures
// (IOException) and timeouts become typed errors here; the caller's cancellation stays a cancellation.
internal sealed class RomLoader(IBoardLink link, TimeProvider clock)
{
    public const ushort Esp32S3ChipId = 9;
    public const int BlockSize = 0x400;

    private const byte FlashBegin = 0x02;
    private const byte FlashData = 0x03;
    private const byte SyncCommand = 0x08;
    private const byte WriteRegisterCommand = 0x09;
    private const byte ReadRegisterCommand = 0x0a;
    private const byte SpiSetParameters = 0x0b;
    private const byte SpiAttach = 0x0d;
    private const byte FlashMd5 = 0x13;
    private const byte GetSecurityInfo = 0x14;
    private const byte InvalidMessage = 0x05;
    private const byte ChecksumSeed = 0xef;
    private const int ConnectAttempts = 5;
    private const int SyncsPerAttempt = 5;
    private const int BlockAttempts = 3;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMilliseconds(100);

    // ESP32-S3 registers that esptool touches around flashing.
    private const uint UartDeviceNumber = 0x3FCEF14C;
    private const uint UsbJtagSerialDevice = 4;
    private const uint RtcWdtConfig0 = 0x60008098;
    private const uint RtcWdtWriteProtect = 0x600080B0;
    private const uint RtcWdtKey = 0x50D83AA1;
    private const uint RtcSwdConfig = 0x600080B4;
    private const uint RtcSwdWriteProtect = 0x600080B8;
    private const uint RtcSwdKey = 0x8F1D312A;
    private const uint RtcSwdAutoFeed = 1u << 31;
    private const uint RtcOption1 = 0x6000812C;
    private const uint ForceDownloadBoot = 0x1;

    private readonly SlipReader reader = new(link);
    private bool connected;

    private readonly record struct Response(uint Value, byte[] Data);

    // Resets the board into its ROM loader and synchronizes with it, as esptool's connect does.
    public async ValueTask<Result<Unit, FlashError>> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ConnectAttempts; attempt++)
        {
            Discard();
            if (!await link.EnterDownloadModeAsync(attempt, clock, cancellationToken).ConfigureAwait(false) && attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, cancellationToken).ConfigureAwait(false);
            for (var sync = 0; sync < SyncsPerAttempt; sync++)
            {
                Discard();
                var result = await Command(SyncCommand, SyncPayload(), 0, SyncTimeout, cancellationToken).ConfigureAwait(false);
                if (result is Ok<Response>)
                {
                    // The ROM answers SYNC several times; Command skips the extra answers as they arrive.
                    connected = true;
                    return new Ok<Unit>(Unit.Value);
                }
                if (result is Err<FlashError>(ConnectionLost lost)) return new Err<FlashError>(lost);
            }
        }
        return new Err<FlashError>(new BoardNotResponding(
            "The board did not answer as an ESP32-S3 in download mode. Hold BOOT, press and release RESET, release BOOT, and try again."));

        // Boot messages and answers to earlier attempts are not answers to the next SYNC.
        void Discard()
        {
            try { link.DiscardInput(); }
            catch (IOException) { }
            reader.Clear();
        }
    }

    // Checks that the board is an ESP32-S3 and stops the watchdogs that would reset it during a long write over its
    // built-in USB port; then attaches the SPI flash and gives the ROM its size.
    public async ValueTask<Result<Unit, FlashError>> PrepareAsync(uint flashSize, CancellationToken cancellationToken)
    {
        var info = await CheckCommand("read the chip type", GetSecurityInfo, [], 0, 20, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!info.TryGetOk(out var security)) return Fail(info);
        var chip = BinaryPrimitives.ReadUInt32LittleEndian(security.AsSpan(12));
        if (chip != Esp32S3ChipId) return new Err<FlashError>(new WrongChip($"The board is not an ESP32-S3 (chip ID {chip})."));

        var device = await ReadRegisterAsync(UartDeviceNumber, cancellationToken).ConfigureAwait(false);
        if (!device.TryGetOk(out var number)) return Fail(device);
        if ((number & 0xff) == UsbJtagSerialDevice)
        {
            var watchdogs = await DisableWatchdogsAsync(cancellationToken).ConfigureAwait(false);
            if (watchdogs is not Ok<Unit>) return watchdogs;
        }

        var attach = new byte[8]; // pins 0 (the default flash pins), then a zero "legacy" argument
        var attached = await CheckCommand("attach the SPI flash", SpiAttach, attach, 0, 0, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!attached.TryGetOk(out _)) return Fail(attached);
        var parameters = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), flashSize);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(8), 64 * 1024); // block
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(12), 4 * 1024); // sector
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(16), 256); // page
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(20), 0xffff); // status mask
        var set = await CheckCommand("set the flash parameters", SpiSetParameters, parameters, 0, 0, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return set.TryGetOk(out _) ? new Ok<Unit>(Unit.Value) : Fail(set);
    }

    // Erases the part's region, writes it block by block, and compares the flash's MD5 with the data's.
    public async ValueTask<Result<Unit, FlashError>> WriteAsync(FlashPart part, Action<long> written, CancellationToken cancellationToken)
    {
        var data = Pad(part.Data.Span);
        var blocks = (data.Length + BlockSize - 1) / BlockSize;
        var begin = new byte[20]; // size to erase, blocks, block size, offset, not encrypted
        BinaryPrimitives.WriteUInt32LittleEndian(begin, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(4), (uint)blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(8), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(12), part.Offset);
        var erased = await CheckCommand($"erase {part.Name}", FlashBegin, begin, 0, 0, PerMegabyte(40, data.Length), cancellationToken)
            .ConfigureAwait(false);
        if (!erased.TryGetOk(out _)) return Fail(erased);

        for (var sequence = 0; sequence < blocks; sequence++)
        {
            var block = new byte[16 + BlockSize];
            block.AsSpan(16).Fill(0xff);
            var chunk = data.AsSpan(sequence * BlockSize, Math.Min(BlockSize, data.Length - sequence * BlockSize));
            chunk.CopyTo(block.AsSpan(16));
            BinaryPrimitives.WriteUInt32LittleEndian(block, BlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)sequence);
            var checksum = Checksum(block.AsSpan(16));
            Result<byte[], FlashError> sent = new Err<FlashError>(new ConnectionLost("No block was sent."));
            for (var attempt = 0; attempt < BlockAttempts; attempt++)
            {
                sent = await CheckCommand($"write {part.Name}", FlashData, block, checksum, 0, DefaultTimeout, cancellationToken).ConfigureAwait(false);
                if (sent is Ok<byte[]>) break;
            }
            if (!sent.TryGetOk(out _)) return Fail(sent);
            written(Math.Min(part.Data.Length, (sequence + 1L) * BlockSize));
        }

        var request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, part.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)data.Length);
        var md5 = await CheckCommand($"check {part.Name}", FlashMd5, request, 0, 32, PerMegabyte(8, data.Length), cancellationToken).ConfigureAwait(false);
        if (!md5.TryGetOk(out var digest)) return Fail(md5);
        var expected = Convert.ToHexStringLower(MD5.HashData(data));
        var actual = System.Text.Encoding.ASCII.GetString(digest).ToLowerInvariant();
        return actual == expected
            ? new Ok<Unit>(Unit.Value)
            : new Err<FlashError>(new VerificationFailed($"{part.Name} at 0x{part.Offset:x} reads back differently than it was written."));
    }

    // Leaves download mode and starts the firmware, clearing the forced download boot first as esptool does. A board
    // that has already left the ROM loader does not answer the register write, which does not matter here.
    public async ValueTask<bool> RestartAsync(CancellationToken cancellationToken)
    {
        await WriteRegisterAsync(RtcOption1, 0, ForceDownloadBoot, cancellationToken).ConfigureAwait(false);
        try { return await link.RestartAsync(clock, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { return false; }
    }

    private async ValueTask<Result<Unit, FlashError>> DisableWatchdogsAsync(CancellationToken cancellationToken)
    {
        (uint Address, uint Value)[] writes =
        [
            (RtcWdtWriteProtect, RtcWdtKey), (RtcWdtConfig0, 0), (RtcWdtWriteProtect, 0),
            (RtcSwdWriteProtect, RtcSwdKey),
        ];
        foreach (var (address, value) in writes)
        {
            var written = await WriteRegisterAsync(address, value, 0xffffffff, cancellationToken).ConfigureAwait(false);
            if (written is not Ok<Unit>) return written;
        }
        var swd = await ReadRegisterAsync(RtcSwdConfig, cancellationToken).ConfigureAwait(false);
        if (!swd.TryGetOk(out var config)) return Fail(swd);
        var fed = await WriteRegisterAsync(RtcSwdConfig, config | RtcSwdAutoFeed, 0xffffffff, cancellationToken).ConfigureAwait(false);
        if (fed is not Ok<Unit>) return fed;
        return await WriteRegisterAsync(RtcSwdWriteProtect, 0, 0xffffffff, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<uint, FlashError>> ReadRegisterAsync(uint address, CancellationToken cancellationToken)
    {
        var request = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(request, address);
        var response = await Exchange("read a register", ReadRegisterCommand, request, 0, 0, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return response.TryGetOk(out var value) ? new Ok<uint>(value.Value) : Fail(response);
    }

    private async ValueTask<Result<Unit, FlashError>> WriteRegisterAsync(uint address, uint value, uint mask, CancellationToken cancellationToken)
    {
        var request = new byte[16]; // address, value, mask, delay
        BinaryPrimitives.WriteUInt32LittleEndian(request, address);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), mask);
        var written = await CheckCommand("write a register", WriteRegisterCommand, request, 0, 0, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return written.TryGetOk(out _) ? new Ok<Unit>(Unit.Value) : Fail(written);
    }

    // Sends a command and checks the status bytes that follow responseLength bytes of data: the first is 0 on
    // success, and the second is the ROM's reason otherwise.
    private async ValueTask<Result<byte[], FlashError>> CheckCommand(string action, byte command, byte[] data, uint checksum,
        int responseLength, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await Exchange(action, command, data, checksum, responseLength, timeout, cancellationToken).ConfigureAwait(false);
        return response.TryGetOk(out var value) ? new Ok<byte[]>(value.Data[..responseLength]) : Fail(response);
    }

    private async ValueTask<Result<Response, FlashError>> Exchange(string action, byte command, byte[] data, uint checksum,
        int responseLength, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await Command(command, data, checksum, timeout, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetOk(out var value)) return response;
        var body = value.Data;
        if (body.Length < responseLength + 2)
        {
            return body.Length >= 2 && body[0] != 0
                ? Rejected(action, body[0], body[1])
                : new Err<FlashError>(new ConnectionLost($"The board's answer to {action} was too short."));
        }
        return body[responseLength] == 0 ? response : Rejected(action, body[responseLength], body[responseLength + 1]);
    }

    private async ValueTask<Result<Response, FlashError>> Command(byte command, byte[] data, uint checksum, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var packet = new byte[8 + data.Length];
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checksum);
        data.CopyTo(packet, 8);
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await link.WriteAsync(Slip.Encode(packet), linked.Token).ConfigureAwait(false);
            // Answers to earlier commands, such as the extra SYNC answers, are skipped, as esptool does.
            for (var skipped = 0; skipped < 100; skipped++)
            {
                var frame = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame.Length < 8 || frame[0] != 1) continue;
                var body = frame[8..];
                if (frame[1] == command) return new Ok<Response>(new(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)), body));
                if (body.Length >= 2 && body[0] != 0 && body[1] == InvalidMessage)
                    return new Err<FlashError>(new BoardRejected($"The board does not support command 0x{command:x2}.", body[0], body[1]));
            }
            return new Err<FlashError>(new ConnectionLost("The board kept answering other commands."));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Err<FlashError>(connected
                ? new ConnectionLost($"The board did not answer within {timeout.TotalSeconds:0.#} seconds.")
                : new BoardNotResponding("The board did not answer."));
        }
        catch (IOException exception)
        {
            return new Err<FlashError>(new ConnectionLost($"The connection to the board failed: {exception.Message}"));
        }
    }

    private static byte[] SyncPayload()
    {
        var payload = new byte[36];
        payload[0] = 0x07;
        payload[1] = 0x07;
        payload[2] = 0x12;
        payload[3] = 0x20;
        payload.AsSpan(4).Fill(0x55);
        return payload;
    }

    // Images are padded with erased bytes to a multiple of four, as esptool pads them before computing the MD5.
    private static byte[] Pad(ReadOnlySpan<byte> data)
    {
        var padded = new byte[(data.Length + 3) & ~3];
        padded.AsSpan().Fill(0xff);
        data.CopyTo(padded);
        return padded;
    }

    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint state = ChecksumSeed;
        foreach (var value in data) state ^= value;
        return state;
    }

    // esptool's timeout_per_mb: seconds per megabyte, but never less than the default.
    private static TimeSpan PerMegabyte(double seconds, int bytes) =>
        TimeSpan.FromSeconds(Math.Max(DefaultTimeout.TotalSeconds, seconds * bytes / 1e6));

    private static Err<FlashError> Rejected(string action, byte status, byte reason) =>
        new(new BoardRejected($"The board could not {action}: {ReasonText(reason)}.", status, reason));

    // The ROM loader's reasons, from esptool's error table.
    private static string ReasonText(byte reason) => reason switch
    {
        0x05 => "the message was invalid",
        0x06 => "it failed to act on the message",
        0x07 => "the message had an invalid checksum",
        0x08 => "writing the flash failed",
        0x09 => "reading the flash failed",
        0x0a => "the flash read length was wrong",
        0x0b => "decompression failed",
        _ => $"reason 0x{reason:x2}",
    };

    // The error of a failed step, to return from a step of another type.
    private static Err<FlashError> Fail<T>(Result<T, FlashError> result) =>
        new(result.TryGetErr(out var error) ? error! : throw new InvalidOperationException("Expected a failed result."));
}
