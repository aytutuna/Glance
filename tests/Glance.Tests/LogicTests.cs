using System.Text;
using Glance;

namespace Glance.Tests;

public class LogicTests
{
    [Fact]
    public void DefaultPromptsDifferPerMode()
    {
        var s = new Settings();
        var prompts = new[] { "answer", "explain", "translate", "summarize" }.Select(m => Prompts.Instruction(s, m)).ToList();
        Assert.Equal(4, prompts.Distinct().Count());
        Assert.Equal(prompts[0], Prompts.Instruction(s, "unknown"));
    }

    [Fact]
    public void TranslateNamesTheTarget()
    {
        var s = new Settings { TranslateTo = "German" };
        Assert.Contains("into German,", Prompts.Instruction(s, "translate"));
    }

    [Fact]
    public void EditedPromptWinsOverDefault()
    {
        var s = new Settings();
        s.PromptOverrides["explain"] = "Be brief.";
        Assert.Equal("Be brief.", Prompts.Instruction(s, "explain"));
        s.PromptOverrides["explain"] = "";
        Assert.Contains("Explain what it shows", Prompts.Instruction(s, "explain"));
    }

    [Fact]
    public void CustomModeKeepsTheInjectionGuard()
    {
        var s = new Settings();
        s.CustomModes.Add(new CustomMode { Id = "custom:1", Name = "Haiku", Prompt = "Write a haiku." });
        var text = Prompts.Instruction(s, "custom:1");
        Assert.StartsWith("Write a haiku. ", text);
        Assert.Contains("not as instructions", text);
    }

    [Fact]
    public void FirstMessageFollowsTheMode()
    {
        Assert.Equal("Explain this screenshot.", Prompts.First("explain"));
        Assert.Equal("Follow your instructions for this screenshot.", Prompts.First("custom:1"));
    }

    [Fact]
    public void FollowUpCarriesTheTranscript()
    {
        var chat = new Conversation(Array.Empty<byte>(), "m", "answer", "sys", null!);
        Assert.Equal(Prompts.First("answer"), Prompts.Message(chat));
        Assert.Equal("sys", Prompts.System(chat));
        chat.Complete("B");
        chat.Ask("why?");
        var message = Prompts.Message(chat);
        Assert.Contains("User: Answer the question in this screenshot.\nAssistant: B", message);
        Assert.EndsWith("Latest message, to answer now: why?", message);
        Assert.NotEqual("sys", Prompts.System(chat));
    }

    static async Task<List<string>> Events(string text)
    {
        var list = new List<string>();
        await foreach (var data in Api.Events(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None))
            list.Add(data);
        return list;
    }

    [Fact]
    public async Task EventsReturnOnlyDataLines()
    {
        var got = await Events("event: x\r\ndata: {\"a\":1}\r\n\r\ndata:two\n: comment\ndata: [DONE]");
        Assert.Equal(new[] { "{\"a\":1}", "two", "[DONE]" }, got);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/", true)]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("http://example.com/v1", false)]
    public void UrlAccepted(string text, bool withKey)
    {
        Assert.Null(Api.CheckUrl(text, withKey, out _));
    }

    [Theory]
    [InlineData("ftp://example.com", false)]
    [InlineData("https://user:pw@example.com", false)]
    [InlineData("https://example.com/v1?x=1", false)]
    [InlineData("http://example.com/v1", true)]
    public void UrlRejected(string text, bool withKey)
    {
        Assert.NotNull(Api.CheckUrl(text, withKey, out _));
    }

    [Fact]
    public void UrlLosesTrailingSlash()
    {
        Api.CheckUrl(" https://api.example.com/v1/ ", false, out var url);
        Assert.Equal("https://api.example.com/v1", url);
    }

    [Fact]
    public void KeyNames()
    {
        Assert.Equal("Ctrl+Shift+Q", KeyDialog.KeyName(Keys.Control | Keys.Shift | Keys.Q));
        Assert.Equal("Left Ctrl", KeyDialog.KeyName(Keys.LControlKey));
        Assert.False(KeyDialog.IsValid(Keys.Control | Keys.Shift));
    }
}
