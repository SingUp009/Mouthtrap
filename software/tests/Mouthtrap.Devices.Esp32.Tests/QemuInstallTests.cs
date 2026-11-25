using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;
using Mouthtrap.Tests;

namespace Mouthtrap.Devices.Tests;

// Sets up the Wi-Fi firmware and its settings in Espressif's QEMU with XiaoProvisioner, through the ROM loader, then
// boots it and reads its log. QEMU's ROM answers like a board in download mode, but has no boot or reset lines, and its serial port is slow:
// a run takes a few minutes. Set MOUTHTRAP_QEMU_BUILD to a build of firmware/xiao-wifi with test/sdkconfig.qemu and
// put qemu-system-xtensa on PATH, as firmware/xiao-wifi/test/test_firmware_qemu.py expects.
public sealed class QemuInstallTests : IDisposable
{
    private static readonly string? Build = Environment.GetEnvironmentVariable("MOUTHTRAP_QEMU_BUILD");
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-qemu-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Provision_WifiFirmwareAndSettings_BootsWithTheSettings()
    {
        Assert.SkipWhen(Build is null, "Set MOUTHTRAP_QEMU_BUILD to run the QEMU tests.");
        // Given an erased 8 MB flash in QEMU's download mode, and the firmware with settings for it
        var settings = ResultAssert.Ok(WifiSettings.Create("Mouthtrap test", "password123", StreamToken.Generate()));
        var firmware = ResultAssert.Ok(FirmwarePackage.FromDirectory(Build!));
        var plan = ResultAssert.Ok(firmware.With(new(NvsImage.Offset, NvsImage.Create(settings), "Wi-Fi settings")));
        var flash = Path.Combine(directory, "flash.bin");
        await File.WriteAllBytesAsync(flash, Enumerable.Repeat((byte)0xff, 8 * 1024 * 1024).ToArray(), TestContext.Current.CancellationToken);
        var port = FreePort();
        var progress = new List<ProvisioningProgress>();
        // QEMU's eFuses are blank, so its chip reports no PSRAM; the PSRAM requirement is the one check left out.
        var provisioner = new XiaoProvisioner(FirmwareVariant.XiaoWifi, new Folder(Build!), $"socket://127.0.0.1:{port}",
            new WifiSetup(settings, Path.Combine(directory, "stream-token.txt")))
        { RequiredPsramBytes = null };
        var confirmation = new Confirmation();

        // When
        Exit<ProvisionedDevice, ProvisioningError> exit;
        using (var qemu = Qemu(flash, $"-global driver=esp32s3.gpio,property=strap_mode,value=0x07 -serial tcp:127.0.0.1:{port},server=on,wait=off"))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            exit = await provisioner.Provision(confirmation, new Collect(progress))
                .RunExitAsync(new ProvisioningEnvironment(TimeProvider.System), TestContext.Current.CancellationToken);
            qemu.Kill();
            await qemu.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        // Then every part is in the flash, and the firmware reads the settings when it starts
        // The board as esptool's flash_id reads QEMU's: a GD 8 MB flash (ID 0x1740c8) and blank eFuses. QEMU has no
        // reset line, so the board has to be restarted by hand.
        var report = Assert.Single(confirmation.Asked);
        Assert.Equal(("00:00:00:00:00:00", "ESP32-S3 (QFN56) (revision v0.0)", (long?)(8 * 1024 * 1024), (long?)null),
            (report.Identity, report.Chip, report.FlashBytes, report.MemoryBytes));
        if (exit is not Ok<ProvisionedDevice>(var device)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.True(device.Input is InputNotConfirmed { Reason: InputNotConfirmedReason.RestartByHand }, $"Got {device.Input}.");
        Assert.Equal(1, progress.Last(report => report.Stage == ProvisioningStage.Writing).Fraction);
        var written = await File.ReadAllBytesAsync(flash, TestContext.Current.CancellationToken);
        foreach (var part in plan.Parts) Assert.True(written.AsSpan((int)part.Offset, part.Data.Length).SequenceEqual(part.Data.Span), part.Name);
        var log = Path.Combine(directory, "serial.log");
        using (var qemu = Qemu(flash, $"-serial file:{log}"))
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !(File.Exists(log) && File.ReadAllText(log).Contains("Settings loaded; connecting to Wi-Fi.")))
                await Task.Delay(200, TestContext.Current.CancellationToken);
            qemu.Kill();
        }
        Assert.Contains("Settings loaded; connecting to Wi-Fi.", File.ReadAllText(log));
    }

    private static Process Qemu(string flash, string arguments) => Process.Start(new ProcessStartInfo("qemu-system-xtensa",
        $"-nographic -machine esp32s3 -m 8M -monitor none -drive file={flash},if=mtd,format=raw {arguments}")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // An ESP-IDF build folder, read through its flash_args.
    private sealed class Folder(string path) : IFirmwareSource
    {
        public Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant) =>
            Effect<ProvisioningEnvironment>.FromResultAsync<LoadedFirmware, ProvisioningError>(_ => ValueTask.FromResult<Result<LoadedFirmware, ProvisioningError>>(
                FirmwarePackage.FromDirectory(path).TryGetOk(out var plan)
                    ? new Ok<LoadedFirmware>(new("qemu", plan!))
                    : new Err<ProvisioningError>(new FirmwareInvalid("The build folder cannot be read."))));
    }

    private sealed class Confirmation : IDeviceConfirmation
    {
        public List<DeviceReport> Asked { get; } = [];

        public ValueTask<bool> ConfirmAsync(DeviceReport report, CancellationToken cancellationToken)
        {
            Asked.Add(report);
            return ValueTask.FromResult(true);
        }
    }

    // Progress<T> posts to the thread pool; this records each report in order as it is made.
    private sealed class Collect(List<ProvisioningProgress> reports) : IProgress<ProvisioningProgress>
    {
        public void Report(ProvisioningProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
