using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Firmware;
using Mouthtrap.Tests;

namespace Mouthtrap.Firmware.Tests;

public sealed class BoardInstallerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // A bootloader-like ESP32-S3 image of three blocks and a bit, and a small data part.
    private static readonly byte[] Bootloader = Image(3 * RomLoader.BlockSize + 10);
    private static readonly byte[] Data = [0xaa, 0x50, 1, 2, 3];
    private static readonly FlashPlan Plan = ResultAssert.Ok(FlashPlan.Create(
        [new(0x0, Bootloader, "bootloader"), new(0x8000, Data, "partition table")], 8 * 1024 * 1024));

    [Fact]
    public async Task Install_Plan_WritesEveryPartAndRestartsTheBoard()
    {
        // Given
        var rom = new FakeRom();
        var progress = new List<FlashProgress>();

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken, progress: progress);

        // Then
        var report = Ok(exit);
        Assert.True(report.Restarted);
        Assert.Null(report.Camera);
        Assert.True(rom.Flash.AsSpan(0, Bootloader.Length).SequenceEqual(Bootloader));
        Assert.True(rom.Flash.AsSpan(0x8000, Data.Length).SequenceEqual(Data));
        Assert.Equal(1, rom.Restarts);
        Assert.Equal((uint)(8 * 1024 * 1024), rom.FlashSize);
        Assert.Equal([FlashStage.Connecting, FlashStage.Preparing, FlashStage.Writing, FlashStage.Restarting],
            progress.Select(report => report.Stage).Distinct());
        Assert.Equal(Plan.TotalBytes, progress.Last(report => report.Stage == FlashStage.Writing).Written);
        Assert.True(rom.Disposed);
    }

    [Fact]
    public async Task Install_Plan_ChecksTheChipBeforeAnythingIsErased()
    {
        // Given
        var rom = new FakeRom();

        // When
        Ok(await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken));

        // Then
        Assert.True(rom.Commands.IndexOf(FakeRom.SecurityInfo) < rom.Commands.IndexOf(FakeRom.FlashBegin));
    }

    [Fact]
    public async Task Install_BoardOnItsBuiltInUsbPort_DisablesTheWatchdogsBeforeWriting()
    {
        // Given the console on USB-Serial-JTAG, whose watchdogs would reset the board during a long write
        var rom = new FakeRom { ConsoleDevice = 4 };

        // When
        Ok(await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken));

        // Then
        Assert.Contains((0x600080B0u, 0x50D83AA1u, 0xffffffffu), rom.RegisterWrites);
        Assert.Contains((0x60008098u, 0u, 0xffffffffu), rom.RegisterWrites);
        Assert.Contains((0x600080B4u, 0x1234u | 1u << 31, 0xffffffffu), rom.RegisterWrites);
        Assert.True(rom.Commands.IndexOf(FakeRom.WriteRegister) < rom.Commands.IndexOf(FakeRom.FlashBegin));
    }

    [Fact]
    public async Task Install_BoardOnAUart_LeavesTheWatchdogsAndOnlyClearsForcedDownloadOnRestart()
    {
        // Given
        var rom = new FakeRom { ConsoleDevice = 0 };

        // When
        Ok(await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken));

        // Then
        Assert.Equal([(0x6000812Cu, 0u, 1u)], rom.RegisterWrites);
    }

    [Fact]
    public async Task Install_BoardNeverAnswers_FailsWithBoardNotRespondingAfterEveryReset()
    {
        // Given a board that is not in download mode, and timeouts that expire at once
        var rom = new FakeRom { Silent = true };

        // When
        var exit = await Run(rom, new ExpiringTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(BoardNotResponding), $"Got {exit}.");
        Assert.Equal(5, rom.ResetsToDownloadMode);
        Assert.Equal(0, rom.Restarts);
        Assert.True(rom.Disposed);
    }

    [Fact]
    public async Task Install_OtherChip_FailsWithWrongChipBeforeErasing()
    {
        // Given an ESP32-C3
        var rom = new FakeRom { ChipId = 5 };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(WrongChip), $"Got {exit}.");
        Assert.DoesNotContain(FakeRom.FlashBegin, rom.Commands);
    }

    [Fact]
    public async Task Install_RomRefusesTheErase_FailsWithItsStatusAndReason()
    {
        // Given
        var rom = new FakeRom { Reject = (FakeRom.FlashBegin, 0x08, int.MaxValue) };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(BoardRejected(_, 1, 0x08)), $"Got {exit}.");
        Assert.Equal(0, rom.Restarts);
    }

    [Fact]
    public async Task Install_BlockRefusedTwice_RetriesItAndSucceeds()
    {
        // Given
        var rom = new FakeRom { Reject = (FakeRom.FlashData, 0x08, 2) };

        // When
        Ok(await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken));

        // Then
        Assert.True(rom.Flash.AsSpan(0, Bootloader.Length).SequenceEqual(Bootloader));
    }

    [Fact]
    public async Task Install_BlockRefusedThreeTimes_FailsWithBoardRejected()
    {
        // Given
        var rom = new FakeRom { Reject = (FakeRom.FlashData, 0x08, 3) };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(BoardRejected), $"Got {exit}.");
    }

    [Fact]
    public async Task Install_FlashReadsBackDifferently_FailsWithVerificationFailed()
    {
        // Given
        var rom = new FakeRom { CorruptFlash = true };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(VerificationFailed), $"Got {exit}.");
        Assert.Equal(0, rom.Restarts);
    }

    [Fact]
    public async Task Install_BoardStopsAnsweringWhileWriting_FailsWithConnectionLostAfterTheTimeout()
    {
        // Given
        var rom = new FakeRom { SilentAfterBlocks = 1 };
        var clock = new FakeTimeProvider();

        // When the board stays silent for longer than a block may take, on each of the three attempts
        var run = Run(rom, clock, TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await rom.SilentReads.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(4));
        }
        var exit = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(ConnectionLost), $"Got {exit}.");
        Assert.True(rom.Disposed);
    }

    [Fact]
    public async Task Install_ConnectionClosesWhileWriting_FailsWithConnectionLost()
    {
        // Given
        var rom = new FakeRom { CloseAfterBlocks = 1 };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(ConnectionLost), $"Got {exit}.");
    }

    [Fact]
    public async Task Install_PortCannotBeOpened_FailsWithPortUnavailable()
    {
        // Given a TCP port that nothing listens on
        var board = BoardLink.Open("socket://127.0.0.1:1");

        // When
        var exit = await BoardInstaller.Install(board, Plan).RunExitAsync(new FirmwareEnvironment(new FakeTimeProvider()),
            TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Err<FlashError>(PortUnavailable), $"Got {exit}.");
    }

    [Fact]
    public async Task Install_CallerCancelsWhileWriting_IsInterruptedAndReleasesTheLink()
    {
        // Given
        var rom = new FakeRom { SilentAfterBlocks = 1 };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // When
        var run = Run(rom, new FakeTimeProvider(), cancellation.Token);
        await rom.SilentReads.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var exit = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        Assert.True(exit is Interrupted, $"Got {exit}.");
        Assert.True(rom.Disposed);
    }

    [Fact]
    public async Task Install_WatchCamera_ReturnsTheUrlTheFirmwareLogs()
    {
        // Given the Wi-Fi firmware's log, coloured as ESP-IDF prints it
        var rom = new FakeRom
        {
            LogAfterRestart = ["I (310) mouthtrap: Settings loaded; connecting to Wi-Fi.", "\u001b[0;32mI (4200) mouthtrap: Camera URL: http://192.168.1.20/stream\u001b[0m"]
        };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken, watchCamera: true);

        // Then
        var camera = Ok(exit).Camera;
        Assert.NotNull(camera);
        Assert.Equal(new Uri("http://192.168.1.20/stream"), ResultAssert.Ok(camera.Value).Address);
    }

    [Fact]
    public async Task Install_WatchCameraAndWifiFails_ReturnsTheFirmwaresError()
    {
        // Given
        var rom = new FakeRom
        {
            LogAfterRestart = ["E (46000) mouthtrap: Wi-Fi did not connect; check the stored settings and reset to retry."]
        };

        // When
        var exit = await Run(rom, new FakeTimeProvider(), TestContext.Current.CancellationToken, watchCamera: true);

        // Then
        var camera = Ok(exit).Camera;
        Assert.NotNull(camera);
        var error = ResultAssert.Err(camera.Value);
        Assert.True(error is FirmwareFailed { Message: "Wi-Fi did not connect; check the stored settings and reset to retry." }, $"Got {error}.");
    }

    [Fact]
    public async Task Install_WatchCameraAndNothingIsLogged_ReturnsCameraUrlNotSeenAfterTheTimeout()
    {
        // Given
        var rom = new FakeRom();
        var clock = new DueTimeAwareTimeProvider(BoardInstaller.CameraTimeout);

        // When the camera stays silent for a minute after the restart
        var run = Run(rom, clock, TestContext.Current.CancellationToken, watchCamera: true);
        await clock.TimerCreated.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        clock.Advance(BoardInstaller.CameraTimeout);
        var exit = await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // Then
        var camera = Ok(exit).Camera;
        Assert.NotNull(camera);
        Assert.True(ResultAssert.Err(camera.Value) is CameraUrlNotSeen);
    }

    [Theory]
    [InlineData("I (4200) mouthtrap: Camera URL: http://10.0.0.5/stream", "http://10.0.0.5/stream")]
    [InlineData("\u001b[0;32mI (4200) mouthtrap: Camera URL: http://10.0.0.5/stream\u001b[0m", "http://10.0.0.5/stream")]
    public void ReadLine_CameraUrl_ReturnsTheUrl(string line, string url)
    {
        // When
        var result = BoardInstaller.ReadLine(System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", ""));

        // Then
        Assert.NotNull(result);
        Assert.Equal(new Uri(url), ResultAssert.Ok(result.Value).Address);
    }

    [Theory]
    [InlineData("I (310) mouthtrap: Settings loaded; connecting to Wi-Fi.")]
    [InlineData("E (900) wifi: some driver message")]
    [InlineData("")]
    public void ReadLine_OtherLine_ReturnsNull(string line)
    {
        // When / Then
        Assert.Null(BoardInstaller.ReadLine(line));
    }

    private static Task<Exit<InstallReport, FlashError>> Run(FakeRom rom, TimeProvider clock, CancellationToken cancellationToken,
        bool watchCamera = false, List<FlashProgress>? progress = null)
    {
        var board = Resource.Make(Effect<FirmwareEnvironment>.Succeed<IBoardLink>(rom), (link, _) => Effect<FirmwareEnvironment>.Sync(() =>
        {
            link.Dispose();
            return Unit.Value;
        }));
        return BoardInstaller.Install(board, Plan, watchCamera, progress is null ? null : new Collect(progress))
            .RunExitAsync(new FirmwareEnvironment(clock), cancellationToken).AsTask();
    }

    private static InstallReport Ok(Exit<InstallReport, FlashError> exit) =>
        exit is Ok<InstallReport>(var report) ? report : throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");

    private static byte[] Image(int length)
    {
        var image = new byte[length];
        Random.Shared.NextBytes(image);
        image[0] = 0xe9;
        image[12] = 9; // ESP32-S3
        image[13] = 0;
        return image;
    }

    private sealed class Collect(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
