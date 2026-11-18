using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Glance.Tests;

public class StringsTests
{
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Glance.csproj")))
            dir = dir.Parent!;
        return dir.FullName;
    }

    static Dictionary<string, string> Read(string file)
    {
        return XDocument.Load(file).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
    }

    static string Holes(string text)
    {
        return string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).OrderBy(v => v));
    }

    [Fact]
    public void EveryLanguageHasEveryKeyWithTheSamePlaceholders()
    {
        string root = Root();
        var english = Read(Path.Combine(root, "Strings.resx"));
        var files = Directory.GetFiles(root, "Strings.*.resx");
        Assert.True(files.Length >= 15);
        foreach (var file in files)
        {
            var other = Read(file);
            Assert.Equal(english.Keys.OrderBy(k => k), other.Keys.OrderBy(k => k));
            foreach (var (key, text) in english)
                Assert.True(Holes(text) == Holes(other[key]), $"{Path.GetFileName(file)}: {key}");
        }
    }
}
