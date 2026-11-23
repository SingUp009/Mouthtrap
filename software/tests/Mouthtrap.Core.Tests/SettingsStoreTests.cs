using System.Text;
using Moderato.Results;
using Mouthtrap.Core;
using Mouthtrap.Tests;
using Xunit;

namespace Mouthtrap.Core.Tests;

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
        Assert.Equal(new PreprocessSettings(ResultAssert.Ok(CropRegion.Create(.24, .25, .48, .58)), QuarterTurn.HalfTurn, false), ResultAssert.Ok(loaded.Preprocessing.ToPreprocess()));
        Assert.Equal(new ExpressionOptions { Smoothing = false, Deadzone = .1f, Tongue = true }, loaded.Expressions.ToOptions());
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
    public void Parse_OmittedParts_UsesDefaults()
    {
        // When
        var loaded = Parse("""{ "version": 1, "preprocessing": { "rotation": 90 } }""");

        // Then
        Assert.Equal(AppSettings.Default with { Preprocessing = new() { Rotation = 90 } }, Loaded(loaded));
        Assert.Equal(PreprocessSettings.Default, ResultAssert.Ok(AppSettings.Default.Preprocessing.ToPreprocess()));
        Assert.Equal(ExpressionOptions.Default, AppSettings.Default.Expressions.ToOptions());
    }

    [Fact]
    public void ToCrop_CropTurnedOff_UsesFullFrame()
    {
        // Given
        var off = Daily.Preprocessing with { UseCrop = false };

        // When
        var crop = ResultAssert.Ok(off.ToCrop());

        // Then
        Assert.Equal(CropRegion.FullFrame, crop);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{ "input": {} }""")]
    [InlineData("""{ "version": "1" }""")]
    [InlineData("""{ "version": 1, "input": null }""")]
    [InlineData("""{ "version": 1, "input": { "kind": "Webcam" } }""")]
    [InlineData("""{ "version": 1, "input": { "kind": 1 } }""")]
    [InlineData("""{ "version": 1, "input": { "camera": { "name": "C200" } } }""")]
    [InlineData("""{ "version": 1, "preprocessing": { "rotation": 90.5 } }""")]
    [InlineData("""{ "version": 1, "osc": { "endpoint": null } }""")]
    [InlineData("""{ "version": 1, "language": "French" }""")]
    [InlineData("""{ "version": 1, "language": 2 }""")]
    public void Parse_MalformedJson_FailsAsCorrupt(string json)
    {
        // When
        var result = Parse(json);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsFileCorrupt));
    }

    [Fact]
    public void Save_Language_WritesNameAndDefaultsToSystem()
    {
        // Given
        Assert.Equal(AppLanguage.System, AppSettings.Default.Language);
        Assert.Equal(AppLanguage.English, Loaded(Parse("""{ "version": 1, "language": "English" }""")).Language);

        // When
        Assert.True(SettingsStore.Save(File, Daily) is Ok<Unit>);

        // Then
        Assert.Contains("\"language\": \"Japanese\"", System.IO.File.ReadAllText(File), StringComparison.Ordinal);
    }

    [Fact]
    public void Culture_Language_NamesUiCulture()
    {
        // When / Then
        Assert.Null(AppLanguages.Culture(AppLanguage.System));
        Assert.Equal("en", AppLanguages.Culture(AppLanguage.English)!.Name);
        Assert.Equal("ja", AppLanguages.Culture(AppLanguage.Japanese)!.Name);
        Assert.True(SettingsStore.Validate(AppSettings.Default with { Language = (AppLanguage)9 }) is Err<SettingsError>(SettingsValueInvalid("language", _)));
    }

    [Fact]
    public void Parse_TokenValue_FailsAsCorrupt()
    {
        // When
        var result = Parse("""{ "version": 1, "input": { "token": "abcdefghijklmnop" } }""");

        // Then
        Assert.True(result is Err<SettingsError>(SettingsFileCorrupt));
    }

    [Fact]
    public void Parse_NewerVersion_FailsAsUnsupported()
    {
        // When
        var result = Parse("""{ "version": 2, "somethingNew": true }""");

        // Then
        Assert.True(result is Err<SettingsError>(SettingsVersionUnsupported(_, 2)));
    }

    [Theory]
    [InlineData("""{ "version": 1, "preprocessing": { "cropLeft": 60, "cropWidth": 50 } }""", "preprocessing.crop")]
    [InlineData("""{ "version": 1, "preprocessing": { "cropHeight": 0 } }""", "preprocessing.crop")]
    [InlineData("""{ "version": 1, "preprocessing": { "rotation": 45 } }""", "preprocessing.rotation")]
    [InlineData("""{ "version": 1, "expressions": { "deadzone": 0.5 } }""", "expressions.deadzone")]
    [InlineData("""{ "version": 1, "osc": { "endpoint": "localhost:9000" } }""", "osc.endpoint")]
    [InlineData("""{ "version": 1, "osc": { "endpoint": "127.0.0.1" } }""", "osc.endpoint")]
    [InlineData("""{ "version": 1, "input": { "mjpegUrl": "ftp://camera/stream" } }""", "input.mjpegUrl")]
    [InlineData("""{ "version": 1, "input": { "mjpegUrl": "http://user:secret@camera/stream" } }""", "input.mjpegUrl")]
    [InlineData("""{ "version": 1, "input": { "tokenFilePath": "stream-token.txt" } }""", "input.tokenFilePath")]
    [InlineData("""{ "version": 1, "model": { "definitionPath": "" } }""", "model.definitionPath")]
    [InlineData("""{ "version": 1, "input": { "camera": { "name": "C200", "identity": " " } } }""", "input.camera.identity")]
    [InlineData("""{ "version": 1, "input": { "camera": { "name": "C200", "identity": "x", "mode": { "width": 0, "height": 720, "pixelFormat": "JPEG", "fps": 30 } } } }""", "input.camera.mode")]
    public void Parse_OutOfRangeValue_FailsNamingField(string json, string field)
    {
        // When
        var result = Parse(json);

        // Then
        Assert.True(result is Err<SettingsError>(SettingsValueInvalid(var name, _)) && name == field);
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

    [Theory]
    [InlineData("127.0.0.1:9000", true)]
    [InlineData("[::1]:9000", true)]
    [InlineData("127.0.0.1:0", false)]
    [InlineData("127.0.0.1:70000", false)]
    [InlineData("localhost:9000", false)]
    [InlineData("9000", false)]
    [InlineData(null, false)]
    public void TryParse_Endpoint_AcceptsOnlyIpLiteralsWithPort(string? text, bool valid)
    {
        // When
        var parsed = OscEndpoints.TryParse(text, out _);

        // Then
        Assert.Equal(valid, parsed);
    }

    private static AppSettings Loaded(Result<AppSettings, SettingsError> result) =>
        result is Ok<AppSettings>(var settings) ? settings : throw new Xunit.Sdk.XunitException($"Expected settings, got {result}.");

    private static Result<AppSettings, SettingsError> Parse(string json) => SettingsStore.Parse("settings.json", Encoding.UTF8.GetBytes(json));
}
