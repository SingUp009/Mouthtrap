using System.Globalization;
using System.Resources;

namespace Mouthtrap.Inference;

// Messages shown to people, in English with translations in ModelMessages.*.resx. The language follows CurrentUICulture.
internal static class ModelMessages
{
    private static readonly ResourceManager Resources = new(typeof(ModelMessages).FullName!, typeof(ModelMessages).Assembly);

    public static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? throw new InvalidOperationException($"Missing message '{name}'.");

    public static string Format(string name, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), arguments);
}
