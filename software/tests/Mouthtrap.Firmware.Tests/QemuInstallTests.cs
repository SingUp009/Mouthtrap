using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Firmware;
using Mouthtrap.Tests;

namespace Mouthtrap.Firmware.Tests;

// Writes the Wi-Fi firmware and its settings to Espressif's QEMU through the ROM loader, then boots it and reads its
// log. QEMU's ROM answers like a board in download mode, but has no boot or reset lines, and its serial port is slow:
// a run takes a few minutes. Set MOUTHTRAP_QEMU_BUILD to a build of firmware/xiao-wifi with test/sdkconfig.qemu and
// put qemu-system-xtensa on PATH, as firmware/xiao-wifi/test/test_firmware_qemu.py expects.
public sealed class QemuInstallTests : IDisposable
{
    private static readonly string? Build = Environment.GetEnvironmentVariable("MOUTHTRAP_QEMU_BUILD");
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-qemu-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Install_WifiFirmwareAndSettings_BootsWithTheSettings()
    {
        Assert.SkipWhen(Build is null, "Set MOUTHTRAP_QEMU_BUILD to run the QEMU tests.");
        // Given an erased 8 MB flash in QEMU's download mode, and the firmware with settings for it
        var settings = ResultAssert.Ok(WifiSettings.Create("Mouthtrap test", "password123", StreamToken.Generate()));
        var firmware = ResultAssert.Ok(FirmwarePackage.FromDirectory(Build!));
        var plan = ResultAssert.Ok(firmware.With(new(NvsImage.Offset, NvsImage.Create(settings), "settings")));
        var flash = Path.Combine(directory, "flash.bin");
        await File.WriteAllBytesAsync(flash, Enumerable.Repeat((byte)0xff, 8 * 1024 * 1024).ToArray(), TestContext.Current.CancellationToken);
        var port = FreePort();
        var progress = new List<FlashProgress>();

        // When
        Exit<InstallReport, FlashError> exit;
        using (var qemu = Qemu(flash, $"-global driver=esp32s3.gpio,property=strap_mode,value=0x07 -serial tcp:127.0.0.1:{port},server=on,wait=off"))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            exit = await BoardInstaller.Install(BoardLink.Open($"socket://127.0.0.1:{port}"), plan, progress: new Collect(progress))
                .RunExitAsync(new FirmwareEnvironment(TimeProvider.System), TestContext.Current.CancellationToken);
            qemu.Kill();
            await qemu.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        // Then every part is in the flash, and the firmware reads the settings when it starts
        if (exit is not Ok<InstallReport>(var report)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.False(report.Restarted);
        Assert.Equal(plan.TotalBytes, progress[^1].Written);
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

    // Progress<T> posts to the thread pool; this records each report in order as it is made.
    private sealed class Collect(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
