using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Sessions;
using Xunit;

namespace Mouthtrap.Sessions.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mouthtrap-settings-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static AppSettings Daily => new()
    {
        Input = new()
        {
            Kind = InputSourceKind.Mjpeg,
            Camera = new("Anker PowerConf C200", @"\\?\usb#vid_291a", new(1280, 720, "JPEG", 30)),
            ImagePath = @"C:\samples\mouth.png",
            MjpegUrl = "http://192.168.0.99/stream",
            TokenFilePath = @"D:\Mouthtrap\firmware\xiao-wifi\stream-token.txt"
        },
        Model = new() { Enabled = true, DefinitionPath = @"D:\models\babble45.json" },
        Preprocessing = new() { UseCrop = true, CropLeft = 24, CropTop = 25, CropWidth = 48, CropHeight = 58, Rotation = 180 },
        Expressions = new() { Smoothing = false, Deadzone = .1f, Tongue = true },
        Osc = new() { Enabled = true, Endpoint = "192.168.0.20:9000" },
        Language = AppLanguage.Japanese
    };

    [Fact]
    public void Load_SavedSettings_ReturnsEqualSettings()
    {
        // Given
        Assert.True(SettingsStore.Save(File, Daily) is Ok<Unit>);

        // When
        var loaded = Loaded(SettingsStore.Load(File));

        // Then
        Assert.Equal(Daily, loaded);
    }

    [Fact]
    public void Save_ExistingFile_ReplacesItWithoutLeavingTemporaryFile()
    {
        // Given
        Assert.True(SettingsStore.Save(File, AppSettings.Default) is Ok<Unit>);

        // When
        Assert.True(SettingsStore.Save(File, Daily) is Ok<Unit>);

        // Then
        Assert.Equal([File], Directory.GetFiles(directory));
        Assert.Equal(Daily, Loaded(SettingsStore.Load(File)));
    }

    [Fact]
    public void Load_MissingFile_FailsWithMissingFile()
    {
        // When
        var result = SettingsStore.Load(File);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsFileMissing(var path)) && path == File);
    }

    [Fact]
    public void Save_InvalidSettings_WritesNothing()
    {
        // Given
        var invalid = Daily with { Preprocessing = new() { Rotation = 45 } };

        // When
        var result = SettingsStore.Save(File, invalid);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsValueInvalid));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Load_CorruptFile_FailsAndLeavesFileInPlace()
    {
        // Given
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File, """{ "version": 1, "input": """);

        // When
        var result = SettingsStore.Load(File);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsFileCorrupt));
        Assert.Equal("""{ "version": 1, "input": """, System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Load_OversizedFile_FailsAsUnreadable()
    {
        // Given
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File, """{ "version": 1 }""" + new string(' ', SettingsStore.MaxFileBytes));

        // When
        var result = SettingsStore.Load(File);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsFileUnreadable));
    }

    [Fact]
    public void LoadAndSave_LockedFile_FailAndKeepFile()
    {
        // Given
        Assert.True(SettingsStore.Save(File, AppSettings.Default) is Ok<Unit>);
        using (new FileStream(File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // When / Then
            Assert.True(SettingsStore.Load(File) is Err<SettingsError>(SettingsFileUnreadable));
            Assert.True(SettingsStore.Save(File, Daily) is Err<SettingsError>(SettingsWriteFailed));
        }

        // Then
        Assert.Equal([File], Directory.GetFiles(directory));
        Assert.Equal(AppSettings.Default, Loaded(SettingsStore.Load(File)));
    }

    private static AppSettings Loaded(Result<AppSettings, SettingsError> result) =>
        result is Ok<AppSettings>(var settings) ? settings : throw new Xunit.Sdk.XunitException($"Expected settings, got {result}.");
}
