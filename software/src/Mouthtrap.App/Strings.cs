using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Mouthtrap.App;

// The GUI's text, in English with translations in Strings.*.resx. The language follows CurrentUICulture, which App
// sets from the saved language before the window is built.
internal static class Strings
{
    private static readonly ResourceManager Resources = new(typeof(Strings).FullName!, typeof(Strings).Assembly);

    public static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? throw new InvalidOperationException($"Missing text '{name}'.");

    public static string Format(string name, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), arguments);
}

// {app:Text Key} in XAML: the text for Key, looked up once when the element is created.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension() { }
    public TextExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
