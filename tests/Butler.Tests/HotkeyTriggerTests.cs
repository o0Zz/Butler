namespace Butler.Tests;

public sealed class HotkeyTriggerTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-hotkey-").FullName;

    List<Trigger> LoadJson(string json)
    {
        var path = Path.Combine(dir, "butler.json");
        File.WriteAllText(path, json);
        return Trigger.Load(path);
    }

    [Theory]
    [InlineData("ctrl+shift+space", "ctrl+shift+space")]
    [InlineData(" Shift + CTRL + Space ", "ctrl+shift+space")] // any order and case, written in one form
    [InlineData("win+alt+F12", "alt+win+f12")]
    [InlineData("ctrl+alt+shift+7", "ctrl+alt+shift+7")]
    public void Keys_are_read_in_one_form(string text, string form) => Assert.Equal(form, Hotkey.Parse(text)?.ToString());

    [Theory]
    [InlineData("shift+a")]       // typing
    [InlineData("space")]         // no modifier
    [InlineData("ctrl+ctrl+a")]
    [InlineData("ctrl+meta+a")]
    [InlineData("ctrl+f25")]
    [InlineData("ctrl+enter")]
    [InlineData("ctrl+shift")]
    [InlineData("")]
    public void Wrong_keys_are_refused(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void A_hotkey_trigger_fills_the_selected_text_and_pastes_by_default()
    {
        var triggers = LoadJson("""
            [
              { "name": "fix", "on": "hotkey", "keys": "ctrl+shift+space", "model": "sonnet", "prompt": "Fix this:\n${text}" },
              { "name": "en", "on": "hotkey", "keys": "ctrl+shift+e", "output": "clipboard", "prompt": "Translate to English" }
            ]
            """);
        var text = new Dictionary<string, string> { ["text"] = "i has went" };
        Assert.Equal("Fix this:\ni has went", triggers[0].PromptWith(text));
        Assert.Equal("Translate to English\n\nThe selected text:\ni has went", triggers[1].PromptWith(text)); // no ${text}: appended
        Assert.True(HotkeyTrigger.Pastes(triggers[0]));
        Assert.False(HotkeyTrigger.Pastes(triggers[1]));
        Assert.Equal("When you press ctrl+shift+space on a selected text", triggers[0].Kind.Describe(triggers[0]));
    }

    [Theory]
    [InlineData("""[{ "name": "x", "on": "hotkey", "prompt": "p" }]""", "must be like ctrl+shift+space")]
    [InlineData("""[{ "name": "x", "on": "hotkey", "keys": "shift+f", "prompt": "p" }]""", "must be like ctrl+shift+space")]
    [InlineData("""[{ "name": "x", "on": "hotkey", "keys": "ctrl+shift+f", "output": "replace", "prompt": "p" }]""", "paste or clipboard")]
    [InlineData("""[{ "name": "x", "on": "hotkey", "keys": "ctrl+shift+f", "command": "echo" }]""", "runs a prompt, not a command")]
    [InlineData("""[{ "name": "x", "on": "hotkey", "keys": "ctrl+shift+f", "prompt": "${path}" }]""", "use ${text}")]
    public void Rejects_wrong_fields(string json, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => LoadJson(json)).Message);

    [Fact]
    public void Saved_from_the_editor_with_its_fields()
    {
        var path = Path.Combine(dir, "butler.json");
        Trigger.Save(path, [Trigger.FromForm("fix", "hotkey", "Fix: ${text}",
            new Dictionary<string, string> { ["keys"] = "ctrl+shift+space", ["output"] = "" }, model: "light")]);
        var t = Trigger.Load(path).Single();
        Assert.Equal(("ctrl+shift+space", null, "light"), (t.Field("keys"), t.Field("output"), t.Model));
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);
}
