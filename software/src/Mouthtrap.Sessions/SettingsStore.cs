using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Sessions;

// The settings file that the GUI and the CLI share. SettingsFormat in Core decides what the file holds; this class
// only finds, reads and replaces it.
public static class SettingsStore
{
    public const int MaxFileBytes = 64 * 1024;

    // %APPDATA%\Mouthtrap\settings.json, shared by the GUI and the CLI's --saved-settings.
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mouthtrap", "settings.json");

    public static Result<AppSettings, SettingsError> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxFileBytes)
                return Fail(new SettingsFileUnreadable(path, $"The settings file is larger than {MaxFileBytes} bytes."));
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return Fail(new SettingsFileMissing(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Fail(new SettingsFileUnreadable(path, exception.Message)); }
        return SettingsFormat.Parse(path, bytes);
    }

    // Writes a temporary file next to the target and moves it over the target, so a crash or a full disk leaves
    // either the old file or the new one, never a partial file. Invalid settings write nothing.
    public static Result<Unit, SettingsError> Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return SettingsFormat.Serialize(settings).AndThen(bytes => Replace(path, bytes));
    }

    private static Result<Unit, SettingsError> Replace(string path, byte[] bytes)
    {
        string? temporary = null;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
            temporary = null;
            return new Ok<Unit>(Unit.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return new Err<SettingsError>(new SettingsWriteFailed(path, exception.Message)); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Err<SettingsError> Fail(SettingsError error) => new(error);
}
