using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using Moderato.Results;
using Mouthtrap.App;
using Mouthtrap.Sessions;
using Xunit;

namespace Mouthtrap.App.Tests;

// The Firmware card, through the real window. Writing to a board needs one; it is checked by hand.
[Collection(WindowCollection.Name)]
public sealed class FirmwareCardTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mouthtrap-firmware-" + Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task FirmwareKind_UsbWebcam_HidesTheWifiFieldsAndExplainsDownloadMode()
    {
        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            Assert.Equal(Visibility.Visible, ui.Window.FirmwareWifiOptions.Visibility);
            ui.Choose(Strings.Get("FirmwareKindName"), Strings.Get("FirmwareUvc"));
            Assert.Equal(Visibility.Collapsed, ui.Window.FirmwareWifiOptions.Visibility);
            Assert.Equal(Strings.Get("FirmwareHintUvc"), ui.Window.FirmwareHint.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FirmwareWrite_WifiFirmware_IsEnabledOnlyWithAPortANetworkAndAPassword()
    {
        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            var write = Strings.Get("FirmwareWriteButton");
            ui.Window.FirmwarePort.ItemsSource = new[] { "COM9" };
            Assert.False(ui.IsEnabled(AutomationControlType.Button, write));
            ui.Window.FirmwarePort.SelectedIndex = 0;
            Assert.False(ui.IsEnabled(AutomationControlType.Button, write));
            ui.Type(Strings.Get("FirmwareSsidName"), "home");
            Assert.False(ui.IsEnabled(AutomationControlType.Button, write));
            // UI Automation does not type into password fields, so the test sets it as the user's typing does.
            ui.Window.FirmwarePassword.Password = "password123";
            Assert.True(ui.IsEnabled(AutomationControlType.Button, write));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Open_AnySettings_OffersTheLatestPublishedReleaseFirst()
    {
        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            Assert.Equal(Strings.Get("FirmwareLatest"), ui.Window.FirmwareSource.SelectedItem?.ToString());
            Assert.Single(ui.Window.FirmwareSource.Items);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Open_SavedTokenFile_IsTheOneTheHintNames()
    {
        // Given
        var token = Path.Combine(directory, "board", "stream-token.txt");
        Assert.True(SettingsStore.Save(SettingsFile, AppSettings.Default with { Input = AppSettings.Default.Input with { TokenFilePath = token } }) is Ok<Unit>);

        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            Assert.Equal(Strings.Format("FirmwareHintWifi", token), ui.Window.FirmwareHint.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Open_NoTokenFile_HintNamesANewFileNextToTheSettings()
    {
        // When / Then
        await WindowDriver.Run(SettingsFile, "en", ui =>
        {
            Assert.Equal(Strings.Format("FirmwareHintWifi", Path.Combine(directory, "stream-token.txt")), ui.Window.FirmwareHint.Text);
            return Task.CompletedTask;
        });
    }
}
