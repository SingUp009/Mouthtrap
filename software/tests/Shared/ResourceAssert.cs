using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Xunit;

namespace Mouthtrap.Tests;

internal static class ResourceAssert
{
    // Every English (neutral) string has a non-empty translation that uses the same format placeholders.
    public static void Translated(ResourceManager resources, string culture)
    {
        var neutral = Strings(resources, CultureInfo.InvariantCulture);
        var translated = Strings(resources, CultureInfo.GetCultureInfo(culture));
        Assert.NotEmpty(neutral);
        Assert.Equal(neutral.Keys.Order(StringComparer.Ordinal), translated.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, text) in neutral)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
            Assert.Equal(Placeholders(text), Placeholders(translated[key]));
        }
    }

    private static Dictionary<string, string> Strings(ResourceManager resources, CultureInfo culture) =>
        (resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'."))
        .Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);

    private static string[] Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{\d+[^}]*\}").Select(match => match.Value).Order(StringComparer.Ordinal)];
}
