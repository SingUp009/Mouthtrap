using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moderato.Results;
using Mouthtrap.App;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.App.Tests;

// User flows through the real window, driven through UI Automation (see WindowDriver).
[Collection(WindowCollection.Name)]
public sealed class MainWindowInteractionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mouthtrap-interaction-" + Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(directory, "settings.json");
    private string ImageFile => Path.Combine(directory, "still.png");

    public MainWindowInteractionTests()
    {
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null,
            new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 }, 6)));
        using var file = File.Create(ImageFile);
        encoder.Save(file);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Start_StillImageWithoutModel_PreviewsThenStopReleasesTheFile()
    {
        // Given
        Save(StillImage());

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            await ui.Click("Start");
            await ui.UntilText(ui.Window.StatusText, text => text == Strings.Get("Previewing"));
            Assert.Equal(Strings.Format("ProcessedTitle", TrackingPipeline.UnmodeledOutputSize, TrackingPipeline.UnmodeledOutputSize),
                ui.Window.ProcessedTitleText.Text);
            Assert.NotNull(ui.Window.Preview.Source);
            Assert.False(ui.IsEnabled(AutomationControlType.Button, "Start"));

            await ui.Click("Stop");
            await ui.UntilText(ui.Window.StatusText, text => text == Strings.Get("Stopped"));
            Assert.True(ui.IsEnabled(AutomationControlType.Button, "Start"));
            using (File.Open(ImageFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        });
    }

    [Fact]
    public async Task Start_DeadzoneOutOfRange_ShowsWhatToFixAndStaysIdle()
    {
        // Given
        Save(StillImage() with { Model = new() { Enabled = true, DefinitionPath = Path.Combine(directory, "model.json") } });

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            ui.Type("Deadzone", "0.7");
            await ui.Click("Start");
            Assert.Equal(Strings.Get("DeadzoneInvalid"), ui.Window.StatusText.Text);
            Assert.Equal(Strings.Get("StateIdle"), ui.Window.SessionStateText.Text);
            Assert.False(ui.IsEnabled(AutomationControlType.Button, "Stop"));
        });
    }

    [Fact]
    public async Task Start_OscAddressIsAName_ShowsWhatToFixAndStaysIdle()
    {
        // Given
        Save(StillImage() with
        {
            Model = new() { Enabled = true, DefinitionPath = Path.Combine(directory, "model.json") },
            Osc = new() { Enabled = true }
        });

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            ui.Type("OSC address", "localhost:9000");
            await ui.Click("Start");
            Assert.Equal(Strings.Get("OscEndpointInvalid"), ui.Window.StatusText.Text);
            Assert.Equal(Strings.Get("StateIdle"), ui.Window.SessionStateText.Text);
        });
    }

    [Fact]
    public async Task Start_ModelFileMissing_ReportsTheModelAndReturnsToIdle()
    {
        // Given
        var missing = Path.Combine(directory, "missing.json");
        Save(StillImage() with { Model = new() { Enabled = true, DefinitionPath = missing } });

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            await ui.Click("Start");
            await ui.UntilText(ui.Window.SessionStateText, text => text == Strings.Get("StateIdle") &&
                ui.Window.StatusText.Text != Strings.Get("StartingInput"));
            Assert.Equal(Strings.Format("ModelMissing", missing), ui.Window.StatusText.Text);
            Assert.True(ui.IsEnabled(AutomationControlType.Button, "Start"));
        });
    }

    [Fact]
    public async Task InputSource_ImageChosenWithoutFile_AsksForAFileAndCannotStart()
    {
        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            ui.Choose("Input source", "Image file");
            Assert.Equal(Strings.Get("GuidanceFile"), ui.Window.StatusText.Text);
            Assert.False(ui.IsEnabled(AutomationControlType.Button, "Start"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Start_ModelWithOutputsTurnedOff_ShowsTheValuesThatAreSent()
    {
        // Given
        var image = SolidImage(128, 64, 32);
        File.WriteAllBytes(Path.Combine(directory, "identity.onnx"), Convert.FromBase64String(TestModels.Identity));
        var definition = Path.Combine(directory, "model.json");
        // The identity model outputs the normalized channels of the 2x1 input in NCHW order: red, red, green, green,
        // blue, blue.
        File.WriteAllText(definition, """
            {"ModelPath":"identity.onnx","InputName":"image","OutputName":"jaw","Width":2,"Height":1,"Layout":"Nchw",
             "Color":"Rgb","OutputCount":6,"JawOpenIndex":0,"ExpressionIndices":{"MouthSmileLeft":2,"TongueOut":4}}
            """);
        Save(StillImage() with
        {
            Input = new() { Kind = InputSourceKind.Image, ImagePath = image },
            Model = new() { Enabled = true, DefinitionPath = definition },
            Expressions = new() { AdaptiveCalibration = false, Smoothing = false, Deadzone = 0, Tongue = false }
        });
        float Channel(byte value) => value * (1f / 255f);

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            await ui.Click("Start");
            await ui.UntilText(ui.Window.AdditionalExpressionText, text => text.Length > 0);
            Assert.Equal(Strings.Format("JawOpenValue", Channel(128), Strings.Get("Uncalibrated")), ui.Window.JawOpenText.Text);
            Assert.Equal(
                Strings.Format("ExpressionOutput", ArkitShape.MouthSmileLeft, Channel(64), Channel(64)) + "\n" +
                Strings.Format("ExpressionNotSent", ArkitShape.TongueOut, Channel(32)),
                ui.Window.AdditionalExpressionText.Text);

            await ui.Click("Stop");
            await ui.UntilText(ui.Window.StatusText, text => text == Strings.Get("Stopped"));
        });
    }

    [Fact]
    public async Task Apply_CropRotationAndMirror_AppliesAndSavesThem()
    {
        // Given
        Save(StillImage());

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            ui.Toggle("Use a crop");
            ui.Type("Crop left in percent", "10");
            ui.Type("Crop top in percent", "20");
            ui.Type("Crop width in percent", "30");
            ui.Type("Crop height in percent", "40");
            ui.Choose("Rotation angle", "90°");
            ui.Toggle("Mirror");
            await ui.Click("Apply");
            Assert.Equal(Strings.Get("CropApplied"), ui.Window.PreprocessStatusText.Text);
            Assert.Equal(new PreprocessingSettings
            {
                UseCrop = true,
                CropLeft = 10,
                CropTop = 20,
                CropWidth = 30,
                CropHeight = 40,
                Rotation = 90,
                Mirror = true
            }, Saved().Preprocessing);
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not a number")]
    public async Task Apply_CropThatCannotBeUsed_KeepsThePreviousSetting(string width)
    {
        // Given
        Save(StillImage());

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            ui.Toggle("Use a crop");
            ui.Type("Crop width in percent", width);
            await ui.Click("Apply");
            Assert.Equal(Strings.Get("CropInvalid"), ui.Window.PreprocessStatusText.Text);
            Assert.Equal(AppSettings.Default.Preprocessing, Saved().Preprocessing);
        });
    }

    [Fact]
    public async Task Apply_CropTurnedOff_UsesTheFullFrameAndKeepsTheCropForLater()
    {
        // Given
        var crop = new PreprocessingSettings { UseCrop = true, CropLeft = 10, CropTop = 20, CropWidth = 30, CropHeight = 40 };
        Save(StillImage() with { Preprocessing = crop });

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", async ui =>
        {
            ui.Toggle("Use a crop");
            await ui.Click("Apply");
            Assert.Equal(Strings.Get("CropFull"), ui.Window.PreprocessStatusText.Text);
            Assert.Equal(crop with { UseCrop = false }, Saved().Preprocessing);
        });
    }

    private AppSettings StillImage() => AppSettings.Default with
    {
        Input = new() { Kind = InputSourceKind.Image, ImagePath = ImageFile },
        Model = new() { Enabled = false }
    };

    private void Save(AppSettings settings) => Assert.True(SettingsStore.Save(SettingsFile, settings) is Ok<Unit>);

    private AppSettings Saved() => SettingsStore.Load(SettingsFile) is Ok<AppSettings>(var saved) ? saved
        : throw new Xunit.Sdk.XunitException("The settings file cannot be read.");

    // A 2x2 PNG of one colour, so scaling it to the model input keeps the colour exact.
    private string SolidImage(byte red, byte green, byte blue)
    {
        var path = Path.Combine(directory, "solid.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null,
            new byte[] { red, green, blue, red, green, blue, red, green, blue, red, green, blue }, 6)));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
