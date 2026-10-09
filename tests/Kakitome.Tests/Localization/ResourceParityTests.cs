using System.Xml.Linq;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Localization;

/// <summary>ja-JP and en-US must ship the same resource keys, each with a non-empty value.</summary>
public sealed class ResourceParityTests
{
    private static readonly string StringsRoot = Path.Combine(RepositoryPaths.Root, "src", "Kakitome.App", "Strings");

    private static Dictionary<string, string> Load(string culture)
    {
        var doc = XDocument.Load(Path.Combine(StringsRoot, culture, "Resources.resw"));
        return doc.Root!.Elements("data").ToDictionary(
            e => (string)e.Attribute("name")!,
            e => (string?)e.Element("value") ?? string.Empty,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Japanese_and_English_have_identical_keys()
    {
        var en = Load("en-US");
        var ja = Load("ja-JP");

        Assert.Empty(en.Keys.Except(ja.Keys));
        Assert.Empty(ja.Keys.Except(en.Keys));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Every_resource_has_a_value(string culture)
    {
        var empty = Load(culture).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key);
        Assert.Empty(empty);
    }

    [Fact]
    public void Keys_have_at_most_one_property_part()
    {
        // "Name.Property" is an x:Uid key; any other '.' makes the resource system look for a property path, so
        // code-looked-up keys (e.g. Stage_audio_analyze) must not contain dots.
        var bad = Load("en-US").Keys
            .Where(k => !k.Contains("[using:", StringComparison.Ordinal) && k.Count(c => c == '.') > 1)
            .Concat(Load("en-US").Keys.Where(k => k.StartsWith("Stage_", StringComparison.Ordinal) && k.Contains('.', StringComparison.Ordinal)));
        Assert.Empty(bad);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Resources_never_use_the_retired_vNext_name(string culture)
    {
        Assert.DoesNotContain(Load(culture).Values, v => v.Contains("vNext", StringComparison.OrdinalIgnoreCase));
    }
}
