using Microsoft.Extensions.Time.Testing;
using Moderato.Effects;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Devices;
using Mouthtrap.Tests;

namespace Mouthtrap.Devices.Tests;

public sealed class XiaoProvisionerTests
{
    private const string TokenFile = @"C:\Mouthtrap\stream-token.txt";
    private const uint Megabyte = 1024 * 1024;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly WifiSettings Settings = ResultAssert.Ok(WifiSettings.Create("home", "password123", "0123456789abcdef0123456789abcdef"));
    private static readonly WifiSetup Wifi = new(Settings, TokenFile);
    private static readonly string[] CameraLog = ["I (4200) mouthtrap: Camera URL: http://192.168.1.20/stream"];

    [Fact]
    public async Task Provision_WifiCamera_WritesTheFirmwareAndSettingsAndReportsTheStream()
    {
        // Given
        var rom = new FakeRom { LogAfterRestart = CameraLog };
        var provisioner = Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi };

        // When
        var device = Ok(await Provision(provisioner));

        // Then
        Assert.Equal("1.2.0", device.FirmwareVersion);
        var stream = Assert.IsType<MjpegStreamReady>(device.Input);
        Assert.Equal(new Uri("http://192.168.1.20/stream"), stream.Address.Address);
        Assert.Equal(TokenFile, stream.TokenFile);
        Assert.True(rom.Flash.AsSpan((int)NvsImage.Offset, NvsImage.Size).SequenceEqual(NvsImage.Create(Settings)));
        Assert.True(rom.Flash.AsSpan(0x10000, Packages.App.Length).SequenceEqual(Packages.App));
    }

    [Fact]
    public async Task Provision_AnyBoard_AsksWithWhatTheBoardReportsBeforeErasing()
    {
        // Given
        var rom = new FakeRom { LogAfterRestart = CameraLog };
        var confirmation = new Confirmation(true, () => Assert.DoesNotContain(FakeRom.FlashBegin, rom.Commands));

        // When
        Ok(await Provision(Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }, confirmation: confirmation));

        // Then
        var report = Assert.Single(confirmation.Asked);
        Assert.Equal(("34:85:18:aa:bb:cc", "ESP32-S3 (QFN56) (revision v0.2)", (long?)(8 * Megabyte), (long?)(8 * Megabyte)),
            (report.Identity, report.Chip, report.FlashBytes, report.MemoryBytes));
        Assert.True(report.CanBeWritten);
    }

    [Fact]
    public async Task Provision_UserDeclines_FailsWithDeviceNotConfirmedAndWritesNothing()
    {
        // Given
        var rom = new FakeRom();

        // When
        var exit = await Provision(Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }, confirmation: new Confirmation(false));

        // Then
        Assert.True(exit is Err<ProvisioningError>(DeviceNotConfirmed), $"Got {exit}.");
        Assert.DoesNotContain(FakeRom.FlashBegin, rom.Commands);
        Assert.Equal(0, rom.Restarts);
        Assert.True(rom.Disposed);
    }

    [Theory]
    [MemberData(nameof(UnsuitableBoards))]
    public async Task Provision_BoardTheFirmwareCannotRunOn_FailsWithDeviceIncompatibleWithoutAsking(string board, DeviceProblemKind problem)
    {
        // Given
        var rom = board switch
        {
            "4 MB flash" => new FakeRom { FlashId = 0x1640ef },
            "unknown flash" => new FakeRom { FlashId = 0xffffff },
            "2 MB PSRAM" => new FakeRom { Efuse = new() { [0x60007054] = 2 << 3 } },
            _ => new FakeRom { Efuse = [] },
        };
        var provisioner = Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi };
        var confirmation = new Confirmation(true);

        // When
        var exit = await Provision(provisioner, confirmation: confirmation);

        // Then
        Assert.True(exit is Err<ProvisioningError>(DeviceIncompatible), $"Got {exit}.");
        Assert.Empty(confirmation.Asked);
        Assert.DoesNotContain(FakeRom.FlashBegin, rom.Commands);
        var report = provisioner.Describe(Esp32S3Board.FromEfuse(rom.Efuse.GetValueOrDefault(0x60007044u), rom.Efuse.GetValueOrDefault(0x60007048u),
            rom.Efuse.GetValueOrDefault(0x60007050u), rom.Efuse.GetValueOrDefault(0x60007054u), rom.Efuse.GetValueOrDefault(0x60007058u), 0, rom.FlashId),
            ResultAssert.Ok(FlashPlan.Create([new(0, Packages.Bootloader, "bootloader")], 8 * Megabyte)));
        Assert.Equal([problem], report.Problems.Select(item => item.Kind));
    }

    public static TheoryData<string, DeviceProblemKind> UnsuitableBoards => new()
    {
        { "4 MB flash", DeviceProblemKind.FlashTooSmall },
        { "unknown flash", DeviceProblemKind.FlashUnknown },
        { "2 MB PSRAM", DeviceProblemKind.MemoryMissing },
        { "no PSRAM", DeviceProblemKind.MemoryMissing },
    };

    [Fact]
    public async Task Provision_UsbWebcamThatWindowsLists_WritesNoSettingsAndReportsTheCamera()
    {
        // Given
        var rom = new FakeRom();
        var cameras = new Cameras(found: true);

        // When
        var device = Ok(await Provision(Board(rom, FirmwareVariant.XiaoUvc, Packages.Webcam()) with { Cameras = cameras }));

        // Then
        Assert.Equal(new UsbCameraReady(XiaoProvisioner.UsbCameraName), device.Input);
        Assert.Equal([(XiaoProvisioner.UsbCameraName, XiaoProvisioner.UsbCameraTimeout)], cameras.Asked);
        Assert.All(rom.Flash.AsSpan((int)NvsImage.Offset, NvsImage.Size).ToArray(), value => Assert.Equal(0xff, value));
    }

    [Fact]
    public async Task Provision_UsbWebcamThatWindowsNeverLists_ReportsTheInputNotConfirmed()
    {
        // Given
        var rom = new FakeRom();

        // When
        var device = Ok(await Provision(Board(rom, FirmwareVariant.XiaoUvcMultimode, Packages.Webcam("xiao-uvc-multimode")) with
        {
            Cameras = new Cameras(found: false)
        }));

        // Then
        Assert.True(device.Input is InputNotConfirmed { Reason: InputNotConfirmedReason.NotReported, TokenFile: null }, $"Got {device.Input}.");
    }

    [Fact]
    public async Task Provision_WifiCameraThatCannotJoinTheNetwork_ReportsTheBoardsProblemAndTheTokenFile()
    {
        // Given
        var rom = new FakeRom { LogAfterRestart = ["E (46000) mouthtrap: Wi-Fi did not connect; check the stored settings and reset to retry."] };

        // When
        var device = Ok(await Provision(Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }));

        // Then
        Assert.Equal(new InputNotConfirmed(InputNotConfirmedReason.DeviceReportedProblem,
            "Wi-Fi did not connect; check the stored settings and reset to retry.", TokenFile), device.Input);
    }

    [Fact]
    public async Task Provision_Progress_ReportsTheStagesInOrderAndEndsWritten()
    {
        // Given
        var progress = new List<ProvisioningProgress>();

        // When
        Ok(await Provision(Board(new FakeRom { LogAfterRestart = CameraLog }, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }, progress: new Collect(progress)));

        // Then
        Assert.Equal([ProvisioningStage.LoadingFirmware, ProvisioningStage.Connecting, ProvisioningStage.Confirming, ProvisioningStage.Writing,
            ProvisioningStage.Restarting, ProvisioningStage.WaitingForInput], progress.Select(report => report.Stage).Distinct());
        Assert.Equal(1, progress.Last(report => report.Stage == ProvisioningStage.Writing).Fraction);
    }

    [Theory]
    [MemberData(nameof(FlashFailures))]
    public async Task Provision_BoardFails_FailsWithTheDeviceNeutralError(string failure, Type expected)
    {
        // Given
        var rom = failure switch
        {
            "chip" => new FakeRom { ChipId = 5 },
            "verify" => new FakeRom { CorruptFlash = true },
            "reject" => new FakeRom { Reject = (FakeRom.FlashBegin, 0x08, int.MaxValue) },
            _ => new FakeRom { Silent = true },
        };

        // When; only the silent board needs its timeouts to expire, and it needs them to expire at once
        TimeProvider clock = failure == "silent" ? new ExpiringTimeProvider() : new FakeTimeProvider();
        var exit = await Provision(Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }, clock);

        // Then
        Assert.True(exit is Err<ProvisioningError>(var error) && error.Value?.GetType() == expected,
            $"Got {(exit is Err<ProvisioningError>(var actual) ? actual.Value : exit)}.");
    }

    public static TheoryData<string, Type> FlashFailures => new()
    {
        { "silent", typeof(DeviceNotResponding) },
        { "chip", typeof(DeviceIncompatible) },
        { "verify", typeof(DeviceWriteFailed) },
        { "reject", typeof(DeviceWriteFailed) },
    };

    [Fact]
    public async Task Provision_PortCannotBeOpened_FailsWithDeviceUnavailable()
    {
        // Given a TCP port that nothing listens on
        var provisioner = new XiaoProvisioner(FirmwareVariant.XiaoWifi, new Source(Packages.Zip()), "socket://127.0.0.1:1", Wifi);

        // When / Then
        Assert.True(await Provision(provisioner) is Err<ProvisioningError>(DeviceUnavailable));
    }

    [Fact]
    public async Task Provision_FirmwareCannotBeLoaded_FailsWithoutOpeningTheBoard()
    {
        // Given
        var opened = false;
        var provisioner = new XiaoProvisioner(FirmwareVariant.XiaoWifi, new FirmwareFile("missing.zip"), "COM9", Wifi)
        {
            OpenBoard = port =>
            {
                opened = true;
                return BoardLink.Open(port);
            }
        };

        // When
        var exit = await Provision(provisioner);

        // Then
        Assert.True(exit is Err<ProvisioningError>(FirmwareUnavailable), $"Got {exit}.");
        Assert.False(opened);
    }

    [Fact]
    public void Provision_WifiCameraWithoutSettings_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentException>(() => Board(new FakeRom(), FirmwareVariant.XiaoWifi).Provision(new Confirmation(true)));
    }

    [Fact]
    public void Provision_WebcamWithWifiSettings_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentException>(() => (Board(new FakeRom(), FirmwareVariant.XiaoUvc) with { Wifi = Wifi, Cameras = new Cameras(true) })
            .Provision(new Confirmation(true)));
    }

    [Fact]
    public void Provision_WebcamWithoutACameraLocator_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentException>(() => Board(new FakeRom(), FirmwareVariant.XiaoUvc).Provision(new Confirmation(true)));
    }

    private static XiaoProvisioner Board(FakeRom rom, FirmwareVariant variant, byte[]? zip = null) =>
        new(variant, new Source(zip ?? Packages.Zip()), "COM9")
        {
            OpenBoard = _ => Resource.Make(Effect<ProvisioningEnvironment>.Succeed<IBoardLink>(rom), (link, _) => Effect<ProvisioningEnvironment>.Sync(() =>
            {
                link.Dispose();
                return Unit.Value;
            }))
        };

    private static async Task<Exit<ProvisionedDevice, ProvisioningError>> Provision(XiaoProvisioner provisioner, TimeProvider? clock = null,
        IProgress<ProvisioningProgress>? progress = null, IDeviceConfirmation? confirmation = null) =>
        await provisioner.Provision(confirmation ?? new Confirmation(true), progress)
            .RunExitAsync(new ProvisioningEnvironment(clock ?? new FakeTimeProvider()), TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

    private static ProvisionedDevice Ok(Exit<ProvisionedDevice, ProvisioningError> exit) =>
        exit is Ok<ProvisionedDevice>(var device) ? device : throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");

    // A release zip held in memory, read as FirmwareFile reads one from disk.
    private sealed class Source(byte[] zip) : IFirmwareSource
    {
        public Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant) =>
            Effect<ProvisioningEnvironment>.FromResultAsync<LoadedFirmware, ProvisioningError>(_ => ValueTask.FromResult(FirmwareFile.Read(zip, variant)));
    }

    // The user's answer; onAsk runs while the question is asked, with the board connected.
    private sealed class Confirmation(bool answer, Action? onAsk = null) : IDeviceConfirmation
    {
        public List<DeviceReport> Asked { get; } = [];

        public ValueTask<bool> ConfirmAsync(DeviceReport report, CancellationToken cancellationToken)
        {
            Asked.Add(report);
            onAsk?.Invoke();
            return ValueTask.FromResult(answer);
        }
    }

    private sealed class Cameras(bool found) : IUsbCameraLocator
    {
        public List<(string Name, TimeSpan Timeout)> Asked { get; } = [];

        public ValueTask<bool> WaitForAsync(string name, TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
        {
            Asked.Add((name, timeout));
            return ValueTask.FromResult(found);
        }
    }

    private sealed class Collect(List<ProvisioningProgress> reports) : IProgress<ProvisioningProgress>
    {
        public void Report(ProvisioningProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
