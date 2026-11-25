using System.IO.Ports;
using System.Net.Sockets;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// A byte connection to a board, and the boot and reset lines when it has them. Every transport failure surfaces as
// an IOException, and reads end with cancellation, whatever the transport does on its own.
public interface IBoardLink : IDisposable
{
    // Reads what has arrived into buffer, waiting for at least one byte; 0 when the connection has ended.
    ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken);
    void DiscardInput();
    // Restarts the board into its ROM loader through the boot and reset lines; attempt chooses the sequence. False when
    // the link has no lines, so the user has to hold BOOT while resetting the board.
    ValueTask<bool> EnterDownloadModeAsync(int attempt, TimeProvider clock, CancellationToken cancellationToken);
    // Restarts the board into the firmware; false when the link cannot.
    ValueTask<bool> RestartAsync(TimeProvider clock, CancellationToken cancellationToken);
}

public static class BoardLink
{
    public const string SocketPrefix = "socket://";

    // A board on a serial port such as COM3, or on socket://host:port, as Espressif's QEMU serves it. The link is owned
    // by whoever runs the resource and closed however the run ends.
    public static Resource<IBoardLink, FlashError, ProvisioningEnvironment> Open(string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        var open = port.StartsWith(SocketPrefix, StringComparison.OrdinalIgnoreCase)
            ? Effect<ProvisioningEnvironment>.FromResultAsync<IBoardLink, FlashError>(cancellationToken => OpenSocket(port, cancellationToken))
            : Effect<ProvisioningEnvironment>.FromResultAsync<IBoardLink, FlashError>(_ => ValueTask.FromResult(OpenSerial(port)));
        return Resource.Make(open, (link, _) => Effect<ProvisioningEnvironment>.Sync(() =>
        {
            link.Dispose();
            return Unit.Value;
        }));
    }

    // The serial ports Windows lists now, sorted, for the user to choose from.
    public static string[] SerialPorts() => [.. SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase)];

    private static Result<IBoardLink, FlashError> OpenSerial(string name)
    {
        var port = new SerialPort(name, 115200) { ReadTimeout = 50, WriteTimeout = 3000, DtrEnable = false, RtsEnable = false };
        try
        {
            port.Open();
            return new Ok<IBoardLink>(new SerialBoardLink(port));
        }
        // Opening: a port that does not exist, is in use, or is not a serial port.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            return new Err<FlashError>(new PortUnavailable($"{name} cannot be opened: {exception.Message}"));
        }
    }

    private static async ValueTask<Result<IBoardLink, FlashError>> OpenSocket(string port, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(port, UriKind.Absolute, out var address) || address.Port <= 0)
            return new Err<FlashError>(new PortUnavailable($"{port} is not a socket://host:port address."));
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address.Host, address.Port, cancellationToken).ConfigureAwait(false);
            return new Ok<IBoardLink>(new StreamBoardLink(client));
        }
        catch (SocketException exception)
        {
            client.Dispose();
            return new Err<FlashError>(new PortUnavailable($"{port} cannot be reached: {exception.Message}"));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

// A serial port. Its reads run on the thread pool with a short read timeout, because SerialPort's own asynchronous
// reads do not observe cancellation on Windows.
internal sealed class SerialBoardLink(SerialPort port) : IBoardLink
{
    public async ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken) =>
        await Task.Run(() =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return port.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { }
                catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
                {
                    throw new IOException(exception.Message, exception);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { port.Write(data, 0, data.Length); }
        // A write that does not finish in WriteTimeout, or a port that disappeared with the board.
        catch (Exception exception) when (exception is TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new IOException(exception.Message, exception);
        }
        return ValueTask.CompletedTask;
    }

    public void DiscardInput()
    {
        try { port.DiscardInBuffer(); }
        catch (InvalidOperationException exception) { throw new IOException(exception.Message, exception); }
    }

    // esptool's sequences: USBJTAGSerialReset for the ESP32-S3's built-in USB port, which the XIAO uses, and
    // ClassicReset for boards with a USB-to-serial chip.
    public async ValueTask<bool> EnterDownloadModeAsync(int attempt, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (attempt < 3)
        {
            SetRts(false);
            SetDtr(false); // idle
            await Wait(100);
            SetDtr(true); // IO0 low
            SetRts(false);
            await Wait(100);
            // Through (1,1) rather than (0,0), so that the chip is held in reset with IO0 low.
            SetRts(true);
            SetDtr(false);
            SetRts(true);
            await Wait(100);
            SetDtr(false);
            SetRts(false); // out of reset
        }
        else
        {
            SetDtr(false);
            SetRts(true);
            await Wait(100);
            SetDtr(true);
            SetRts(false);
            await Wait(attempt == 3 ? 50 : 550);
            SetDtr(false);
        }
        return true;

        Task Wait(int milliseconds) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, cancellationToken);
    }

    public async ValueTask<bool> RestartAsync(TimeProvider clock, CancellationToken cancellationToken)
    {
        SetRts(true);
        await Task.Delay(TimeSpan.FromMilliseconds(100), clock, cancellationToken).ConfigureAwait(false);
        SetRts(false);
        return true;
    }

    public void Dispose() => port.Dispose();

    // usbser.sys passes a new RTS state to the device only with a DTR change, so DTR is set again, as esptool does.
    private void SetRts(bool value)
    {
        Guard(() => port.RtsEnable = value);
        Guard(() => port.DtrEnable = port.DtrEnable);
    }

    private void SetDtr(bool value) => Guard(() => port.DtrEnable = value);

    private static void Guard(Action change)
    {
        try { change(); }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new IOException(exception.Message, exception);
        }
    }
}

// A TCP connection, such as the serial port of Espressif's QEMU. It has no boot or reset lines.
internal sealed class StreamBoardLink(TcpClient client) : IBoardLink
{
    private readonly NetworkStream stream = client.GetStream();

    public async ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        try { return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (SocketException exception) { throw new IOException(exception.Message, exception); }
    }

    public async ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        try { await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false); }
        catch (SocketException exception) { throw new IOException(exception.Message, exception); }
    }

    public void DiscardInput() { }

    public ValueTask<bool> EnterDownloadModeAsync(int attempt, TimeProvider clock, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    public ValueTask<bool> RestartAsync(TimeProvider clock, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public void Dispose() => client.Dispose();
}
