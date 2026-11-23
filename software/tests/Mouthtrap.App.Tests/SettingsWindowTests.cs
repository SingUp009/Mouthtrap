using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Threading;
using Moderato.Results;
using Mouthtrap.App;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.App.Tests;

public sealed class SettingsWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mouthtrap-window-" + Guid.NewGuid().ToString("N"));
    private string SettingsFile => Path.Combine(directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static readonly AppSettings Daily = new()
    {
        Input = new()
        {
            Kind = InputSourceKind.Mjpeg,
            MjpegUrl = "http://192.168.0.99/stream",
            TokenFilePath = @"D:\Mouthtrap\firmware\xiao-wifi\stream-token.txt",
            VideoPath = @"C:\samples\mouth.mp4"
        },
        Model = new() { Enabled = true, DefinitionPath = @"D:\models\babble45.json" },
        Preprocessing = new() { UseCrop = true, CropLeft = 24, CropTop = 25, CropWidth = 48, CropHeight = 58, Rotation = 180 },
        Expressions = new() { Smoothing = false, Deadzone = .1f },
        Osc = new() { Enabled = true, Endpoint = "127.0.0.1:9000" }
    };

    [Fact]
    public async Task Open_SavedSettings_RestoresControlsWithoutRewritingFile()
    {
        // Given
        Assert.True(SettingsStore.Save(SettingsFile, Daily) is Ok<Unit>);
        var written = File.GetLastWriteTimeUtc(SettingsFile);

        // When / Then
        await OnWindow(SettingsFile, "ja", window =>
        {
            Assert.Equal(3, window.InputKind.SelectedIndex);
            Assert.Equal(Visibility.Visible, window.MjpegOptions.Visibility);
            Assert.Equal(Visibility.Collapsed, window.CameraOptions.Visibility);
            Assert.Equal("http://192.168.0.99/stream", window.MjpegUrl.Text);
            Assert.Equal(Daily.Input.TokenFilePath, window.TokenFileText.Text);
            Assert.Equal(true, window.EnableTracking.IsChecked);
            Assert.Equal(Daily.Model.DefinitionPath, window.ModelPathText.Text);
            Assert.Equal(true, window.CropEnabled.IsChecked);
            Assert.Equal(["24", "25", "48", "58"], [window.CropX.Text, window.CropY.Text, window.CropWidth.Text, window.CropHeight.Text]);
            Assert.Equal(2, window.Rotation.SelectedIndex);
            Assert.Equal(false, window.Smoothing.IsChecked);
            Assert.Equal(true, window.OscEnabled.IsChecked);
            Assert.True(window.OscEnabled.IsEnabled);
            Assert.Contains("復元", window.SettingsStatusText.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, window.SettingsStatusText.Visibility);
            Assert.Equal("配信URLとトークンファイルを確認して開始してください。", window.StatusText.Text);
        });

        // Then
        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsFile));
        Assert.Equal(Daily, Loaded());
    }

    [Fact]
    public async Task Close_ChangesAndTextStillBeingEdited_SavesBoth()
    {
        // Given
        Assert.True(SettingsStore.Save(SettingsFile, Daily) is Ok<Unit>);

        // When
        await OnWindow(SettingsFile, "ja", window =>
        {
            window.OscEnabled.IsChecked = false;
            Assert.False(Loaded().Osc.Enabled);
            window.OscEndpoint.Text = "192.168.0.20:9001";
            window.MjpegUrl.Text = "http://192.168.0.98/stream";
        });

        // Then
        var saved = Loaded();
        Assert.Equal(Daily with
        {
            Input = Daily.Input with { MjpegUrl = "http://192.168.0.98/stream" },
            Osc = new() { Enabled = false, Endpoint = "192.168.0.20:9001" }
        }, saved);
    }

    [Fact]
    public async Task Close_InvalidText_KeepsLastSavedValue()
    {
        // Given
        Assert.True(SettingsStore.Save(SettingsFile, Daily) is Ok<Unit>);

        // When
        await OnWindow(SettingsFile, "ja", window =>
        {
            window.OscEndpoint.Text = "localhost:9000";
            window.Deadzone.Text = "0.7";
            window.MjpegUrl.Text = "ftp://camera";
            window.Tongue.IsChecked = true;
        });

        // Then
        Assert.Equal(Daily with { Expressions = Daily.Expressions with { Tongue = true } }, Loaded());
    }

    [Fact]
    public async Task Open_CorruptFile_StartsWithDefaultsAndLeavesFileAlone()
    {
        // Given
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsFile, "{ broken");

        // When / Then
        await OnWindow(SettingsFile, "ja", window =>
        {
            Assert.Equal(0, window.InputKind.SelectedIndex);
            Assert.Equal("0.15", window.Deadzone.Text);
            Assert.Contains("壊れて", window.SettingsStatusText.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, window.SettingsStatusText.Visibility);
            Assert.Equal("カメラとモードを確認して開始してください。", window.StatusText.Text);
        });

        // Then
        Assert.Equal("{ broken", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task Open_NoFile_WritesNothingUntilSomethingChanges()
    {
        // When / Then: opening and closing without changes writes nothing.
        await OnWindow(SettingsFile, "ja", window => Assert.Contains("ありません", window.SettingsStatusText.Text, StringComparison.Ordinal));
        Assert.False(File.Exists(SettingsFile));

        // When / Then: a change is written.
        await OnWindow(SettingsFile, "ja", window =>
        {
            Assert.Equal(Visibility.Collapsed, window.SettingsStatusText.Visibility);
            window.InputKind.SelectedIndex = 2;
            Assert.Equal("ファイルを選択して開始してください。", window.StatusText.Text);
            window.InputKind.SelectedIndex = 3;
            Assert.Equal("配信URLとトークンファイルを確認して開始してください。", window.StatusText.Text);
            window.InputKind.SelectedIndex = 2;
        });
        Assert.Equal(InputSourceKind.Video, Loaded().Input.Kind);
    }

    [Theory]
    [InlineData("en", "_Start", "Check the camera and mode, then press Start.", "Language")]
    [InlineData("ja", "開始 (_S)", "カメラとモードを確認して開始してください。", "言語")]
    public async Task Open_UiLanguage_BuildsWindowInThatLanguage(string culture, string start, string guidance, string languageTitle)
    {
        // When / Then
        await OnWindow(SettingsFile, culture, window =>
        {
            Assert.Equal(start, window.StartButton.Content);
            Assert.Equal(guidance, window.StatusText.Text);
            Assert.Contains(languageTitle, window.LanguageChoice.Parent is FrameworkElement panel
                ? string.Join("|", LogicalTreeHelper.GetChildren(panel).OfType<System.Windows.Controls.TextBlock>().Select(text => text.Text)) : "",
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LanguageChoice_NewLanguage_IsSavedAndAppliesAfterRestart()
    {
        // Given
        Assert.True(SettingsStore.Save(SettingsFile, Daily) is Ok<Unit>);

        // When / Then: choosing another language asks for a restart, and choosing back withdraws it.
        await OnWindow(SettingsFile, "en", window =>
        {
            Assert.Equal(0, window.LanguageChoice.SelectedIndex);
            Assert.Equal(Visibility.Collapsed, window.LanguageHint.Visibility);
            window.LanguageChoice.SelectedIndex = 2;
            Assert.Equal(AppLanguage.Japanese, Loaded().Language);
            Assert.Equal(Visibility.Visible, window.LanguageHint.Visibility);
            Assert.Equal("Restart Mouthtrap to apply the language.", window.LanguageHint.Text);
            window.LanguageChoice.SelectedIndex = 0;
            Assert.Equal(Visibility.Collapsed, window.LanguageHint.Visibility);
        });
        Assert.Equal(Daily, Loaded());

        // When / Then: a saved choice is selected the next time the window opens.
        await OnWindow(SettingsFile, "en", window => window.LanguageChoice.SelectedIndex = 1);
        await OnWindow(SettingsFile, "en", window => Assert.Equal(1, window.LanguageChoice.SelectedIndex));
        Assert.Equal(AppLanguage.English, Loaded().Language);
    }

    [Fact]
    public void Strings_EveryText_HasJapaneseTranslation()
    {
        // Given
        var resources = new ResourceManager("Mouthtrap.App.Strings", typeof(MainWindow).Assembly);

        // When / Then
        ResourceAssert.Translated(resources, "ja");
    }

    private AppSettings Loaded() =>
        SettingsStore.Load(SettingsFile) is Ok<AppSettings>(var settings) ? settings : throw new Xunit.Sdk.XunitException("Settings did not load.");

    // Builds the window on an STA dispatcher in the given UI language, runs the check, then closes it the way the user does.
    private static Task OnWindow(string settingsPath, string culture, Action<MainWindow> check)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                Exception? failure = null;
                dispatcher.UnhandledException += (_, args) =>
                {
                    failure ??= args.Exception;
                    args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                // App sets the UI culture before building the window; the test thread does the same.
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var window = new MainWindow(settingsPath);
                window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                dispatcher.BeginInvoke(() =>
                {
                    try { check(window); }
                    catch (Exception exception) { failure ??= exception; }
                    window.Close();
                });
                Dispatcher.Run();
                if (failure is not null) throw failure;
                finished.TrySetResult();
            }
            catch (Exception exception) { finished.TrySetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
