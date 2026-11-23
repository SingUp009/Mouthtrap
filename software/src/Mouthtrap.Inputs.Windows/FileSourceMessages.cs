using System.Globalization;
using System.Resources;

namespace Mouthtrap.Inputs;

// Messages shown to people, in English with translations in FileSourceMessages.*.resx. The language follows CurrentUICulture.
internal static class FileSourceMessages
{
    private static readonly ResourceManager Resources = new(typeof(FileSourceMessages).FullName!, typeof(FileSourceMessages).Assembly);

    public static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? throw new InvalidOperationException($"Missing message '{name}'.");

    public static string Format(string name, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), arguments);
}
