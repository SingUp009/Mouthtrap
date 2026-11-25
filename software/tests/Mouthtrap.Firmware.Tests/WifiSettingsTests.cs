using Mouthtrap.Firmware;
using Mouthtrap.Tests;

namespace Mouthtrap.Firmware.Tests;

public sealed class WifiSettingsTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string directory = Directory.CreateTempSubdirectory("mouthtrap-settings-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData("a", "12345678")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", "123456789012345678901234567890123456789012345678901234567890123")]
    public void Create_AtEachLimit_ReturnsSettings(string ssid, string password)
    {
        // When / Then
        ResultAssert.Ok(WifiSettings.Create(ssid, password, Token));
    }

    [Theory]
    [InlineData(null, "password123", Token, "Ssid")]
    [InlineData("", "password123", Token, "Ssid")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", "password123", Token, "Ssid")] // 33 bytes
    [InlineData("あいうえおかきくけこさ", "password123", Token, "Ssid")] // 11 characters, 33 bytes in UTF-8
    [InlineData("home\n", "password123", Token, "Ssid")]
    [InlineData("home", "1234567", Token, "Password")]
    [InlineData("home", "1234567890123456789012345678901234567890123456789012345678901234", Token, "Password")]
    [InlineData("home", "password123", "short", "Token")]
    [InlineData("home", "password123", "0123456789abcdef0123456789abcde!", "Token")]
    public void Create_ValueTheFirmwareRefuses_ReturnsItsField(string? ssid, string password, string token, string field)
    {
        // When / Then
        Assert.Equal(field, ResultAssert.Err(WifiSettings.Create(ssid, password, token)).Name);
    }

    [Fact]
    public void ToString_Settings_HidesThePasswordAndTheToken()
    {
        // Given
        var settings = ResultAssert.Ok(WifiSettings.Create("home", "secret-password", Token));

        // When
        var text = settings.ToString();

        // Then
        Assert.Contains("home", text);
        Assert.DoesNotContain("secret-password", text);
        Assert.DoesNotContain(Token, text);
    }

    [Fact]
    public void ReadOrCreate_MissingFile_CreatesARandomTokenInANewFolder()
    {
        // Given
        var path = Path.Combine(directory, "new", "stream-token.txt");

        // When
        var token = ResultAssert.Ok(StreamToken.ReadOrCreate(path));

        // Then
        Assert.True(StreamToken.IsValid(token));
        Assert.Equal(token, File.ReadAllText(path));
        Assert.NotEqual(token, ResultAssert.Ok(StreamToken.ReadOrCreate(Path.Combine(directory, "other.txt"))));
    }

    [Fact]
    public void ReadOrCreate_ExistingFile_ReusesItsTokenTrimmed()
    {
        // Given a token file written by provision.py or PowerShell
        var path = Path.Combine(directory, "stream-token.txt");
        File.WriteAllText(path, "﻿" + Token + "\r\n");

        // When / Then
        Assert.Equal(Token, ResultAssert.Ok(StreamToken.ReadOrCreate(path)));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef 0123456789abcdef")]
    public void ReadOrCreate_FileWithAnInvalidToken_ReturnsInvalidValueAndKeepsTheFile(string content)
    {
        // Given
        var path = Path.Combine(directory, "stream-token.txt");
        File.WriteAllText(path, content);

        // When
        var error = ResultAssert.Err(StreamToken.ReadOrCreate(path));

        // Then
        Assert.Equal("TokenFile", error.Name);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void ReadOrCreate_FileLargerThanTheLimit_ReturnsInvalidValue()
    {
        // Given
        var path = Path.Combine(directory, "stream-token.txt");
        File.WriteAllText(path, new string('a', StreamToken.MaxFileBytes + 1));

        // When / Then
        Assert.Contains("at most", ResultAssert.Err(StreamToken.ReadOrCreate(path)).Message);
    }
}
