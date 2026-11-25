using ConsoleAppFramework;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;
using Mouthtrap.Inputs;
using Mouthtrap.Sessions;

namespace Mouthtrap.Cli;

// The Board firmware card of the GUI, for agents: the same ports, releases, board check and write, with JSON output.
// A write never asks: it goes ahead only for the board whose MAC address --board names, as read with the board command.
public sealed partial class Commands
{
    /// <summary>List the serial ports a board can be written on, as JSON.</summary>
    [Command("ports")]
    public int Ports()
    {
        var ports = BoardLink.SerialPorts();
        Write(Console.Out, new { Type = "ports", Ports = ports });
        return ports.Length == 0 ? 2 : 0;
    }

    /// <summary>List the published releases of Mouthtrap on GitHub, newest first, as JSON. Drafts are not listed.</summary>
    [Command("releases")]
    public async Task<int> Releases(CancellationToken cancellationToken)
    {
        var exit = await FirmwareRelease.Releases().RunExitAsync(new ProvisioningEnvironment(TimeProvider.System), cancellationToken);
        switch (exit)
        {
            case Ok<IReadOnlyList<ReleaseVersion>>(var releases):
                Write(Console.Out, new { Type = "releases", Releases = releases });
                return 0;
            case Err<ProvisioningError>(var error):
                return ProvisioningFailure(error);
            case Die die:
                return Defect(die);
            default:
                return 130;
        }
    }

    /// <summary>Read the board on a serial port without writing it, as JSON: its chip, flash, PSRAM in the chip and MAC address, and
    /// why the firmware cannot run on it, if it cannot. Exit code 0 when it can be written, 3 when it cannot.</summary>
    /// <param name="port">Serial port of the board, such as COM3, from ports.</param>
    [Command("board")]
    public async Task<int> Board(string port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(port)) return Usage("Give --port from ports.");
        var exit = await XiaoBoard.Inspect(port).RunExitAsync(new ProvisioningEnvironment(TimeProvider.System), cancellationToken);
        switch (exit)
        {
            case Ok<DeviceReport>(var report):
                WriteBoard(port, report);
                return report.CanBeWritten ? 0 : 3;
            case Err<ProvisioningError>(var error):
                return ProvisioningFailure(error);
            case Die die:
                return Defect(die);
            default:
                return 130;
        }
    }

    /// <summary>Write Mouthtrap firmware to a XIAO ESP32S3 Sense, as the GUI's Board firmware card does. Progress is JSON Lines;
    /// the last line says how video now arrives. The board is written only when its MAC address is --board.</summary>
    /// <param name="firmware">XiaoWifi, XiaoUvc or XiaoUvcMultimode.</param>
    /// <param name="port">Serial port of the board, from ports.</param>
    /// <param name="board">MAC address of the board, from board; any other board is left as it is.</param>
    /// <param name="version">Release version from releases, such as 1.2.0; omit for the latest published release.</param>
    /// <param name="zip">Firmware zip file to write instead of a release, such as one from a draft release.</param>
    /// <param name="ssid">2.4 GHz Wi-Fi network for XiaoWifi.</param>
    /// <param name="passwordEnv">Environment variable holding the Wi-Fi password for XiaoWifi, so that it is not on the command line.</param>
    /// <param name="tokenFile">Stream token file for XiaoWifi; created if missing (default: the settings' token file, else next to the settings file).</param>
    /// <param name="settings">Settings JSON to update with the input the board reports, as the GUI does.</param>
    /// <param name="savedSettings">Update the GUI's settings (%APPDATA%\Mouthtrap\settings.json) with the input the board reports.</param>
    [Command("flash")]
    public async Task<int> Flash(FirmwareVariant firmware, string port, string board, string? version = null, string? zip = null,
        string? ssid = null, string? passwordEnv = null, string? tokenFile = null, string? settings = null, bool savedSettings = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(firmware)) return Usage("--firmware must be XiaoWifi, XiaoUvc or XiaoUvcMultimode.");
        if (string.IsNullOrWhiteSpace(port)) return Usage("Give --port from ports.");
        if (string.IsNullOrWhiteSpace(board)) return Usage("Give --board, the MAC address that board reports.");
        if (version is not null && zip is not null) return Usage("Use either --version or --zip, not both.");
        if (settings is not null && savedSettings) return Usage("Use either --settings or --saved-settings, not both.");
        var wifi = firmware == FirmwareVariant.XiaoWifi;
        if (!wifi && (ssid ?? passwordEnv ?? tokenFile) is not null) return Usage("--ssid, --password-env and --token-file are for XiaoWifi only.");

        var settingsPath = settings ?? (savedSettings ? SettingsStore.DefaultPath : null);
        var saved = AppSettings.Default;
        if (settingsPath is not null)
        {
            switch (SettingsStore.Load(settingsPath))
            {
                case Ok<AppSettings>(var loaded):
                    saved = loaded;
                    break;
                // A first setup has no settings yet; they are created with the input the board reports.
                case Err<SettingsError>(SettingsFileMissing):
                    break;
                case Err<SettingsError>(var error):
                    Error("settings", SettingsErrorMessage(error));
                    return 2;
            }
        }

        WifiSetup? setup = null;
        if (wifi)
        {
            if (string.IsNullOrEmpty(ssid) || string.IsNullOrEmpty(passwordEnv)) return Usage("XiaoWifi needs --ssid and --password-env.");
            if (Environment.GetEnvironmentVariable(passwordEnv) is not { } password) return Usage($"The environment variable {passwordEnv} is not set.");
            var tokenPath = tokenFile ?? ProvisionedSettings.TokenFile(settingsPath ?? SettingsStore.DefaultPath, saved.Input.TokenFilePath);
            var token = StreamToken.ReadOrCreate(tokenPath);
            if (!token.TryGetOk(out var value))
            {
                Error("token_file", token.TryGetErr(out var tokenError) ? tokenError!.Message : null);
                return 2;
            }
            switch (WifiSettings.Create(ssid, password, value))
            {
                case Ok<WifiSettings>(var created):
                    setup = new(created, tokenPath);
                    break;
                case Err<InvalidValue>(var invalid):
                    return Usage(invalid.Message);
            }
        }

        IFirmwareSource source = zip is not null ? new FirmwareFile(zip) : new FirmwareRelease(version);
        var provisioner = new XiaoProvisioner(firmware, source, port, setup) { Cameras = wifi ? null : new UsbCameraLocator() };
        var confirmation = new ExpectedDeviceConfirmation(board);
        var exit = await provisioner.Provision(confirmation, new ProgressLines())
            .RunExitAsync(new ProvisioningEnvironment(TimeProvider.System), cancellationToken);
        if (confirmation.Reported is { } reported) WriteBoard(port, reported);
        switch (exit)
        {
            case Ok<ProvisionedDevice>(var device):
                Write(Console.Out, new { Type = "provisioned", device.FirmwareVersion, Input = InputJson(device.Input) });
                return settingsPath is null ? 0 : await SaveProvisioned(settingsPath, saved, device.Input, cancellationToken);
            case Err<ProvisioningError>(DeviceNotConfirmed) when confirmation.Reported is { } found:
                Error("board_mismatch", found.CanBeWritten
                    ? $"The board on {port} is {found.Identity}, not {board.Trim()}; nothing was written."
                    : $"The board on {port} cannot take the firmware; nothing was written.");
                return 1;
            case Err<ProvisioningError>(var error):
                return ProvisioningFailure(error);
            case Die die:
                return Defect(die);
            default:
                return 130;
        }
    }

    // Writes the input the board reports into the settings, as the GUI selects it. A USB camera is stored by its
    // system identity, which only the camera list gives.
    private static async Task<int> SaveProvisioned(string path, AppSettings saved, ProvisionedInput input, CancellationToken cancellationToken)
    {
        CameraSettings? camera = null;
        if (input is UsbCameraReady(var name))
        {
            var devices = await Discover(cancellationToken);
            if (devices is null) return cancellationToken.IsCancellationRequested ? 130 : 1;
            if (devices.FirstOrDefault(device => device.Name == name) is { } device) camera = new(device.Name, device.Identity.ToString() ?? "");
        }
        var next = ProvisionedSettings.Apply(saved, input, camera);
        if (SettingsStore.Save(path, next) is Err<SettingsError>(var error))
        {
            Error("settings", SettingsErrorMessage(error));
            return 1;
        }
        Write(Console.Out, new { Type = "settings", Path = path, Input = next.Input.Kind.ToString() });
        return 0;
    }

    private static object InputJson(ProvisionedInput input) => input switch
    {
        MjpegStreamReady(var address, var tokenFile) => new { Kind = "mjpeg", Url = address.ToString(), TokenFile = tokenFile },
        UsbCameraReady(var name) => new { Kind = "camera", Name = name },
        InputNotConfirmed(var reason, var message, var tokenFile) => new
        {
            Kind = "notConfirmed",
            Reason = JsonNamingCamel(reason.ToString()),
            Message = message,
            TokenFile = tokenFile
        },
        _ => throw new InvalidOperationException($"No JSON is defined for {input.GetType().Name}."),
    };

    private static void WriteBoard(string port, DeviceReport report) => Write(Console.Out, new
    {
        Type = "board",
        Port = port,
        report.Identity,
        report.Chip,
        report.FlashBytes,
        report.MemoryBytes,
        report.CanBeWritten,
        Problems = report.Problems.Select(problem => new { Kind = JsonNamingCamel(problem.Kind.ToString()), problem.Message })
    });

    private static int ProvisioningFailure(ProvisioningError error) => error switch
    {
        FirmwareUnavailable(var message, var code) => Failure("firmware_unavailable", message, code),
        FirmwareInvalid(var message) => Failure("firmware_invalid", message),
        DeviceUnavailable(var message) => Failure("device_unavailable", message),
        DeviceNotResponding(var message) => Failure("device_not_responding", message),
        DeviceIncompatible(var message) => Failure("device_incompatible", message),
        DeviceNotConfirmed(var message) => Failure("board_mismatch", message),
        DeviceWriteFailed(var message) => Failure("device_write_failed", message),
    };

    private static string JsonNamingCamel(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    // Progress as JSON Lines, one line per stage and whole percent, written as it is reported so the order is kept.
    private sealed class ProgressLines : IProgress<ProvisioningProgress>
    {
        private (ProvisioningStage Stage, int Percent)? last;

        public void Report(ProvisioningProgress value)
        {
            var percent = (int)Math.Floor(value.Fraction * 100);
            if (last == (value.Stage, percent)) return;
            last = (value.Stage, percent);
            Write(Console.Out, new
            {
                Type = "progress",
                Stage = JsonNamingCamel(value.Stage.ToString()),
                Fraction = Math.Round(value.Fraction, 2, MidpointRounding.ToZero),
                value.Detail
            });
        }
    }
}
