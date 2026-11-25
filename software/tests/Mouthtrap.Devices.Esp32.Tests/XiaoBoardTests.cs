using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;

namespace Mouthtrap.Devices.Tests;

public sealed class XiaoBoardTests
{
    private const long Megabyte = 1024 * 1024;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Inspect_Xiao_ReportsItWithoutWritingOrRestartingAndReleasesTheLink()
    {
        // Given
        var rom = new FakeRom();

        // When
        var exit = await Inspect(rom);

        // Then
        if (exit is not Ok<DeviceReport>(var report)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.Equal(("34:85:18:aa:bb:cc", "ESP32-S3 (QFN56) (revision v0.2)", (long?)(8 * Megabyte), (long?)(8 * Megabyte)),
            (report.Identity, report.Chip, report.FlashBytes, report.MemoryBytes));
        Assert.True(report.CanBeWritten);
        Assert.DoesNotContain(FakeRom.FlashBegin, rom.Commands);
        Assert.DoesNotContain(rom.RegisterWrites, write => write.Address == 0x6000812C);
        Assert.Equal(0, rom.Restarts);
        Assert.True(rom.Disposed);
    }

    [Fact]
    public async Task Inspect_BoardWithoutPsram_ReportsTheProblemAsAResult()
    {
        // Given an ESP32-S3 without PSRAM in the chip
        var rom = new FakeRom { Efuse = [] };

        // When
        var exit = await Inspect(rom);

        // Then
        if (exit is not Ok<DeviceReport>(var report)) throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");
        Assert.False(report.CanBeWritten);
        Assert.Equal([DeviceProblemKind.MemoryMissing], report.Problems.Select(problem => problem.Kind));
    }

    [Fact]
    public async Task Inspect_OtherChip_FailsWithDeviceIncompatible()
    {
        // When
        var exit = await Inspect(new FakeRom { ChipId = 5 });

        // Then
        Assert.True(exit is Err<ProvisioningError>(DeviceIncompatible), $"Got {exit}.");
    }

    [Fact]
    public async Task Inspect_PortCannotBeOpened_FailsWithDeviceUnavailable()
    {
        // When
        var exit = await XiaoBoard.Inspect("socket://127.0.0.1:1").RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<ProvisioningError>(DeviceUnavailable), $"Got {exit}.");
    }

    [Theory]
    [InlineData(8, 8, new DeviceProblemKind[0])]
    [InlineData(16, 8, new DeviceProblemKind[0])]
    [InlineData(4, 8, new[] { DeviceProblemKind.FlashTooSmall })]
    [InlineData(8, 2, new[] { DeviceProblemKind.MemoryMissing })]
    [InlineData(4, 0, new[] { DeviceProblemKind.FlashTooSmall, DeviceProblemKind.MemoryMissing })]
    public void Describe_Board_ListsWhatKeepsTheFirmwareFromRunning(int flashMegabytes, int psramMegabytes, DeviceProblemKind[] problems)
    {
        // Given
        var board = new Esp32S3Board("ESP32-S3 (QFN56) (revision v0.2)", "34:85:18:aa:bb:cc", 0x1740c8, (uint)(flashMegabytes * Megabyte),
            psramMegabytes == 0 ? null : (uint)(psramMegabytes * Megabyte), null);

        // When
        var report = XiaoBoard.Describe(board, XiaoBoard.FlashBytes, XiaoBoard.PsramBytes);

        // Then
        Assert.Equal(problems, report.Problems.Select(problem => problem.Kind));
    }

    private static async Task<Exit<DeviceReport, ProvisioningError>> Inspect(FakeRom rom) =>
        await XiaoBoard.Inspect(Resource.Make(Effect<ProvisioningEnvironment>.Succeed<IBoardLink>(rom), (link, _) => Effect<ProvisioningEnvironment>.Sync(() =>
            {
                link.Dispose();
                return Unit.Value;
            })), XiaoBoard.PsramBytes)
            .RunExitAsync(new ProvisioningEnvironment(new FakeTimeProvider()), TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(Bound, TestContext.Current.CancellationToken);
}
