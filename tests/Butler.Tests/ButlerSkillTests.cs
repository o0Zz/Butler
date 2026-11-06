namespace Butler.Tests;

// The skill switches the process's language: with the other Lang tests, not in parallel.
[Collection("Lang")]
public sealed class ButlerSkillTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-skill-").FullName;
    const string Exe = @"C:\Tools\Butler.exe";

    [Fact]
    public void The_skill_tells_every_kind_field_and_variable_in_english()
    {
        Lang.Use("fr");
        var text = ButlerSkill.Text(new Settings { TriggersFile = @"C:\skills\butler.json" }, Exe);
        Assert.Equal("fr", Lang.Code); // put back
        Assert.StartsWith("---\nname: butler\ndescription: Configure Butler", text);
        Assert.Contains(ButlerSkill.Marker, text);
        Assert.Contains(@"& ""C:\Tools\Butler.exe"" check", text);
        Assert.Contains(@"**C:\skills\butler.json**", text);
        foreach (var k in TriggerKind.All)
        {
            Assert.Contains($"`\"on\": \"{k.Name}\"`", text);
            Assert.Contains(k.Help, text);
            foreach (var f in k.Form) Assert.Contains($"- `{f.Name}`", text);
            foreach (var v in k.Variables) Assert.Contains("`${" + v + "}`", text);
        }
        Assert.Contains("(Hotkey)", text);                  // not "Raccourci clavier"
        Assert.Contains("`fast` = haiku, `balanced` = sonnet, `powerful` = opus", text);
        Assert.Contains("`fast` = the CLI's default", ButlerSkill.Text(new Settings { Cli = "copilot" }, Exe));
        Lang.Use("en");
    }

    [Fact]
    public void Written_when_it_changed_and_never_over_the_users_own()
    {
        var path = ButlerSkill.PathIn(dir);
        Assert.True(ButlerSkill.Write(dir, new Settings(), Exe));
        var written = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(50);
        Assert.True(ButlerSkill.Write(dir, new Settings(), Exe));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // the same: not touched (a git repo)

        ButlerSkill.Write(dir, new Settings { Cli = "copilot" }, Exe);
        Assert.Contains("with copilot now", File.ReadAllText(path));

        File.WriteAllText(path, "---\nname: butler\n---\nMy own notes.");
        Assert.False(ButlerSkill.Write(dir, new Settings(), Exe));
        Assert.Equal("---\nname: butler\n---\nMy own notes.", File.ReadAllText(path));
    }

    [Fact]
    public void Check_says_what_the_tray_would_refuse()
    {
        var triggers = Path.Combine(dir, "butler.json");
        var settings = Path.Combine(dir, "settings.json");
        new Settings { TriggersFile = triggers }.Save(settings);

        File.WriteAllText(triggers, """[{ "name": "brief", "on": "schedule", "schedule": "daily 08:30", "prompt": "/brief" }]""");
        Assert.Equal((true, $"OK: 1 triggers in {triggers}"), Program.Check(settings));

        File.WriteAllText(triggers, """[{ "name": "brief", "on": "schedule", "schedule": "every day", "prompt": "/brief" }]""");
        var (ok, message) = Program.Check(settings);
        Assert.False(ok);
        Assert.StartsWith("Error: ", message);
        Assert.Contains("every day", message);

        File.WriteAllText(triggers, "[{ \"name\": ");
        Assert.False(Program.Check(settings).Ok); // not even JSON
        File.Delete(triggers);
        Assert.False(Program.Check(settings).Ok);
    }

    public void Dispose()
    {
        Lang.Use("en");
        Directory.Delete(dir, recursive: true);
    }
}
