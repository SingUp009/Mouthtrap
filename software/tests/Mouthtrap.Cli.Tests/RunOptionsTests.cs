using Mouthtrap.Cli;
using Mouthtrap.Core;
using Xunit;

namespace Mouthtrap.Cli.Tests;

public sealed class RunOptionsTests
{
    private static readonly AppSettings Daily = new()
    {
        Input = new()
        {
            Kind = InputSourceKind.Mjpeg,
            Camera = new("C200", "camera-path", new(1280, 720, "JPEG", 30)),
            ImagePath = @"C:\samples\mouth.png",
            MjpegUrl = "http://192.168.0.99/stream",
            TokenFilePath = @"D:\token.txt"
        },
        Model = new() { Enabled = true, DefinitionPath = @"D:\models\babble45.json" },
        Preprocessing = new() { UseCrop = true, CropLeft = 24, CropTop = 25, CropWidth = 48, CropHeight = 58, Rotation = 180 },
        Expressions = new() { Smoothing = false, Deadzone = .1f },
        Osc = new() { Enabled = true, Endpoint = "127.0.0.1:9000" }
    };

    [Fact]
    public void Resolve_NoSettings_UsesBuiltInDefaults()
    {
        // When
        var options = RunOptions.Resolve(new(), null);

        // Then
        Assert.Equal(new RunOptions(InputKind.Camera, null, null, null, null, 0, 0, 100, 100, 0, false,
            CalibrationMode.Adaptive, SmoothingMode.OneEuro, false, Deadzone.DefaultValue, null, false, null), options);
    }

    [Fact]
    public void Resolve_SavedSettings_FillEveryOptionNotGiven()
    {
        // When
        var options = RunOptions.Resolve(new(), Daily);

        // Then
        Assert.Equal(new RunOptions(InputKind.Mjpeg, null, "http://192.168.0.99/stream", @"D:\token.txt", @"D:\models\babble45.json",
            24, 25, 48, 58, 180, false, CalibrationMode.Adaptive, SmoothingMode.Off, false, .1f, "127.0.0.1:9000", false, null), options);
    }

    [Fact]
    public void Resolve_ExplicitOptions_WinFieldByField()
    {
        // When
        var options = RunOptions.Resolve(new()
        {
            CropLeft = 20,
            Rotation = 0,
            Calibration = CalibrationMode.Off,
            Smoothing = SmoothingMode.OneEuro,
            Deadzone = 0,
            Model = @"D:\other.json",
            Osc = "192.168.0.20:9000",
            Mirror = true,
            Tongue = true
        }, Daily);

        // Then
        Assert.Equal((20d, 25d, 48d, 58d, 0), (options.CropLeft, options.CropTop, options.CropWidth, options.CropHeight, options.Rotation));
        Assert.Equal((CalibrationMode.Off, SmoothingMode.OneEuro, 0f), (options.Calibration, options.Smoothing, options.Deadzone));
        Assert.Equal((@"D:\other.json", "192.168.0.20:9000", true, true), (options.Model, options.Osc, options.Mirror, options.Tongue));
    }

    [Fact]
    public void Resolve_DifferentSource_DoesNotTakeOtherInputsSavedValues()
    {
        // When
        var image = RunOptions.Resolve(new() { Source = InputKind.Image }, Daily);
        var camera = RunOptions.Resolve(new() { Source = InputKind.Camera }, Daily);

        // Then
        Assert.Equal((@"C:\samples\mouth.png", (string?)null, (string?)null, (CameraSettings?)null), (image.Path, image.Url, image.TokenFile, image.Camera));
        Assert.Equal(Daily.Input.Camera, camera.Camera);
        Assert.Null(camera.Path);
    }

    [Fact]
    public void Resolve_ExplicitUrl_DoesNotSendSavedToken()
    {
        // When
        var options = RunOptions.Resolve(new() { Url = "http://192.168.0.50/stream" }, Daily);

        // Then
        Assert.Equal(InputKind.Mjpeg, options.Source);
        Assert.Null(options.TokenFile);
    }

    [Fact]
    public void Resolve_CropTurnedOffAndModelDisabled_UsesNeitherCropModelNorOsc()
    {
        // Given
        var settings = Daily with
        {
            Preprocessing = Daily.Preprocessing with { UseCrop = false },
            Model = Daily.Model with { Enabled = false }
        };

        // When
        var options = RunOptions.Resolve(new(), settings);

        // Then
        Assert.Equal((0d, 0d, 100d, 100d, 180), (options.CropLeft, options.CropTop, options.CropWidth, options.CropHeight, options.Rotation));
        Assert.Null(options.Model);
        Assert.Null(options.Osc);
    }

    [Fact]
    public void Resolve_DryRun_ReplacesSavedOscTarget()
    {
        // When
        var options = RunOptions.Resolve(new() { OscDryRun = true }, Daily);

        // Then
        Assert.Null(options.Osc);
        Assert.True(options.OscDryRun);
    }

    [Theory]
    [InlineData(InputSourceKind.Camera, InputKind.Camera)]
    [InlineData(InputSourceKind.Image, InputKind.Image)]
    [InlineData(InputSourceKind.Video, InputKind.Video)]
    [InlineData(InputSourceKind.Mjpeg, InputKind.Mjpeg)]
    public void Resolve_SavedInputKind_MapsToSameSource(InputSourceKind saved, InputKind expected)
    {
        // When
        var options = RunOptions.Resolve(new(), AppSettings.Default with { Input = new() { Kind = saved } });

        // Then
        Assert.Equal(expected, options.Source);
    }

    [Fact]
    public void SettingsErrorMessage_EveryError_HasMessage()
    {
        // Given
        SettingsError[] errors = [new SettingsFileMissing("a"), new SettingsFileUnreadable("a", "b"), new SettingsFileCorrupt("a", "b"),
            new SettingsVersionUnsupported("a", 2), new SettingsValueInvalid("osc.endpoint", "b"), new SettingsWriteFailed("a", "b")];

        // When / Then
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(Commands.SettingsErrorMessage(error))));
    }
}
