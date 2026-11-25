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
    public async Task Provision_UsbWebcam_WritesNoSettingsAndReportsTheCamera()
    {
        // Given the USB webcam release
        var rom = new FakeRom();
        var webcam = Packages.Zip(folder: "mouthtrap-firmware-xiao-uvc-1.2.0",
            flashArgs: Packages.FlashArgs.Replace("mouthtrap_xiao_wifi.bin", "mouthtrap_xiao_uvc.bin", StringComparison.Ordinal),
            files: [("bootloader/bootloader.bin", Packages.Bootloader), ("mouthtrap_xiao_uvc.bin", Packages.App),
                ("partition_table/partition-table.bin", Packages.PartitionTable)]);

        // When
        var device = Ok(await Provision(Board(rom, FirmwareVariant.XiaoUvc, webcam)));

        // Then
        Assert.Equal(new UsbCameraReady(XiaoProvisioner.UsbCameraName), device.Input);
        Assert.All(rom.Flash.AsSpan((int)NvsImage.Offset, NvsImage.Size).ToArray(), value => Assert.Equal(0xff, value));
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
        Assert.Equal([ProvisioningStage.LoadingFirmware, ProvisioningStage.Connecting, ProvisioningStage.Writing, ProvisioningStage.Restarting,
            ProvisioningStage.WaitingForInput], progress.Select(report => report.Stage).Distinct());
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

        // When
        var exit = await Provision(Board(rom, FirmwareVariant.XiaoWifi) with { Wifi = Wifi }, new ExpiringTimeProvider());

        // Then
        Assert.True(exit is Err<ProvisioningError>(var error) && error.Value?.GetType() == expected, $"Got {exit}.");
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
        Assert.Throws<ArgumentException>(() => Board(new FakeRom(), FirmwareVariant.XiaoWifi).Provision());
    }

    [Fact]
    public void Provision_WebcamWithWifiSettings_Throws()
    {
        // When / Then
        Assert.Throws<ArgumentException>(() => (Board(new FakeRom(), FirmwareVariant.XiaoUvc) with { Wifi = Wifi }).Provision());
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
        IProgress<ProvisioningProgress>? progress = null) =>
        await provisioner.Provision(progress).RunExitAsync(new ProvisioningEnvironment(clock ?? new FakeTimeProvider()), TestContext.Current.CancellationToken);

    private static ProvisionedDevice Ok(Exit<ProvisionedDevice, ProvisioningError> exit) =>
        exit is Ok<ProvisionedDevice>(var device) ? device : throw new Xunit.Sdk.XunitException($"Expected Ok, got {exit}.");

    // A release zip held in memory, read as FirmwareFile reads one from disk.
    private sealed class Source(byte[] zip) : IFirmwareSource
    {
        public Effect<LoadedFirmware, ProvisioningError, ProvisioningEnvironment> Load(FirmwareVariant variant) =>
            Effect<ProvisioningEnvironment>.FromResultAsync<LoadedFirmware, ProvisioningError>(_ => ValueTask.FromResult(FirmwareFile.Read(zip, variant, "zip")));
    }

    private sealed class Collect(List<ProvisioningProgress> reports) : IProgress<ProvisioningProgress>
    {
        public void Report(ProvisioningProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
