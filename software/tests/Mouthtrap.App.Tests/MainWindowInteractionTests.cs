using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moderato.Results;
using Mouthtrap.App;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Xunit;

namespace Mouthtrap.App.Tests;

// User flows through the real window, driven through UI Automation (see WindowDriver).
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

    private AppSettings StillImage() => AppSettings.Default with
    {
        Input = new() { Kind = InputSourceKind.Image, ImagePath = ImageFile },
        Model = new() { Enabled = false }
    };

    private void Save(AppSettings settings) => Assert.True(SettingsStore.Save(SettingsFile, settings) is Ok<Unit>);
}
