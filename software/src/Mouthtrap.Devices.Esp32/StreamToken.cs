using System.Security.Cryptography;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Devices;

// The bearer token that protects the Wi-Fi stream. The board stores it and Mouthtrap sends it, so it lives in a
// small text file that both sides use; reusing the file keeps the stream working when only the network changes.
public static class StreamToken
{
    public const int MaxFileBytes = 256;

    public static bool IsValid(string token) =>
        token.Length is >= 32 and <= 120 && token.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_');

    // 32 random bytes as 64 hexadecimal digits, as provision.py creates them.
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // The token in path, or a new one written to path when the file does not exist yet. The value is never in a
    // message.
    public static Result<string, InvalidValue> ReadOrCreate(string path)
    {
        try
        {
            if (!File.Exists(path)) return Create(path);
            using var file = File.OpenRead(path);
            if (file.Length > MaxFileBytes) return Invalid($"The token file must be at most {MaxFileBytes} bytes.");
            using var reader = new StreamReader(file);
            var token = reader.ReadToEnd().Trim();
            return IsValid(token) ? new Ok<string>(token) : Invalid("The token in the file must be 32-120 letters, digits, '-' or '_'.");
        }
        // Reading or creating the file: missing folder, access denied, in use, or a path that is not valid.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Invalid($"The token file cannot be used: {exception.Message}");
        }
    }

    private static Ok<string> Create(string path)
    {
        var token = Generate();
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder) Directory.CreateDirectory(folder);
        // CreateNew: another program that created the file in the meantime is not overwritten.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file);
        writer.Write(token);
        return new(token);
    }

    private static Err<InvalidValue> Invalid(string message) => new(new("TokenFile", message));
}
