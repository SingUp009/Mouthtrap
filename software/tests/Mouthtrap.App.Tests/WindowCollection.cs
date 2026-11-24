using Xunit;

namespace Mouthtrap.App.Tests;

// Test classes that build MainWindow share this collection, so their windows are never built at the same time. WPF
// loads the window's XAML through shared type caches, and building windows on several STA threads at once
// intermittently fails with XamlParseException ("The given key 'RowDefinitions' was not present in the dictionary").
[CollectionDefinition(Name)]
public sealed class WindowCollection
{
    public const string Name = "Windows";
}
