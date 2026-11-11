using System.Text.Json;
using Glance;

namespace Glance.Tests;

public class SettingsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("glance").FullName;

    string Write(string name, string json)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    Settings Load(string json)
    {
        return Settings.Load(Write("settings.json", json), Path.Combine(dir, "old.json"));
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = Settings.Load(Path.Combine(dir, "none.json"), Path.Combine(dir, "old.json"));
        Assert.Equal(Settings.Claude, s.Provider);
        Assert.Equal(100, s.TextOpacity);
        Assert.True(s.CheckUpdates);
    }

    [Fact]
    public void FieldsAddedLaterKeepTheirDefaults()
    {
        var s = Load("""{"Model":"claude-sonnet-5-5","CenterAnswer":true}""");
        Assert.Equal("claude-sonnet-5-5", s.Model);
        Assert.True(s.CenterAnswer);
        Assert.Equal(100, s.TextOpacity);
        Assert.Empty(s.CustomModes);
        Assert.Equal("", s.TranslateTo);
    }

    [Fact]
    public void NullCollectionsAreReplaced()
    {
        var s = Load("""{"CustomModes":null,"ApiKeys":null,"Order":null,"PromptOverrides":null,"ProviderModels":null}""");
        Assert.Empty(s.CustomModes);
        Assert.Empty(s.ApiKeys);
        Assert.Empty(s.Order);
    }

    [Fact]
    public void BadValuesAreRepaired()
    {
        var s = Load("""{"Model":"gone","Mode":"gone","Provider":"openai","TextOpacity":3,"UseArea":true,"AnswerArea":[0.5,0.5,0.9,0.9]}""");
        Assert.Equal(Settings.Models[0].Id, s.Model);
        Assert.Equal("answer", s.Mode);
        Assert.Equal(Settings.Claude, s.Provider);
        Assert.Equal(10, s.TextOpacity);
        Assert.Null(s.AnswerArea);
        Assert.False(s.UseArea);
    }

    [Fact]
    public void CustomModeSurvivesAsTheMode()
    {
        var s = Load("""{"Mode":"custom:ab","CustomModes":[{"Id":"custom:ab","Name":"Haiku","Prompt":"Write a haiku."}]}""");
        Assert.Equal("custom:ab", s.Mode);
    }

    [Fact]
    public void HotkeysNeverCollide()
    {
        var s = Load("""{"Hotkey":81,"CloseKey":81,"RegionKey":81,"HideKey":0}""");
        var keys = new[] { s.Hotkey, s.CloseKey, s.RegionKey, s.HideKey };
        Assert.Equal(4, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(KeyDialog.IsValid(k)));
    }

    [Fact]
    public void CorruptFileIsKeptAsBad()
    {
        string path = Write("settings.json", "{ not json");
        var s = Settings.Load(path, Path.Combine(dir, "old.json"));
        Assert.Equal(Settings.Claude, s.Provider);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void OldFileIsMigrated()
    {
        string old = Write("old.json", """{"CenterAnswer":true}""");
        string path = Path.Combine(dir, "new", "settings.json");
        var s = Settings.Load(path, old);
        Assert.True(s.CenterAnswer);
        Assert.True(JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!.CenterAnswer);
    }

    [Theory]
    [InlineData(new double[] { 0, 0, 1, 1 }, true)]
    [InlineData(new double[] { 0.1, 0.1, 0.5, 0.5 }, true)]
    [InlineData(new double[] { 0.6, 0, 0.5, 1 }, false)]
    [InlineData(new double[] { 0, 0, 0, 1 }, false)]
    [InlineData(new double[] { 0, 0, 1 }, false)]
    public void AreaValidation(double[] area, bool valid)
    {
        Assert.Equal(valid, Settings.ValidArea(area));
    }
}
