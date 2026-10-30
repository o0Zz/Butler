namespace Butler.Tests;

public class SettingsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-settings-").FullName;
    string File_ => Path.Combine(dir, "settings.json");

    [Fact]
    public void Without_a_file_the_defaults_are_claude_and_the_skills_folder()
    {
        var s = Settings.Load(File_);
        Assert.Equal("claude", s.Cli);
        Assert.EndsWith(@".claude\skills\butler.json", s.TriggersFile);
        Assert.EndsWith(@".claude\skills", s.SkillsFolder);
    }

    [Fact]
    public void Saved_settings_are_read_back_from_a_camel_case_file()
    {
        new Settings { Cli = "copilot", TriggersFile = @"C:\t\triggers.json", SkillsFolder = @"C:\t" }.Save(File_);
        Assert.Contains("\"triggersFile\"", File.ReadAllText(File_));
        Assert.Equal(new Settings { Cli = "copilot", TriggersFile = @"C:\t\triggers.json", SkillsFolder = @"C:\t" }, Settings.Load(File_));

        File.WriteAllText(File_, """{ "cli": "copilot" }"""); // edited by hand: what is missing keeps its default
        Assert.Equal(new Settings { Cli = "copilot" }, Settings.Load(File_));
    }

    [Fact]
    public void Unknown_cli_in_the_file_is_refused()
    {
        File.WriteAllText(File_, """{ "cli": "gpt" }""");
        Assert.Throws<ArgumentException>(() => Settings.Load(File_));
    }

    [Fact]
    public void Problems_name_the_fields_that_cannot_be_saved()
    {
        var triggers = Path.Combine(dir, "butler.json");
        File.WriteAllText(triggers, "[]");

        var bad = new Settings { Cli = "gpt", TriggersFile = Path.Combine(dir, "nope.json"), SkillsFolder = Path.Combine(dir, "nope") }.Problems();
        Assert.Equal(["cli", "skillsFolder", "triggersFile"], bad.Keys.Order());

        var good = new Settings { Cli = "claude", TriggersFile = triggers, SkillsFolder = dir }.Problems();
        Assert.DoesNotContain("triggersFile", good.Keys);
        Assert.DoesNotContain("skillsFolder", good.Keys);
    }

    [Fact]
    public void A_cli_is_installed_when_its_exe_is_on_the_path()
    {
        var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "copilot.cmd"), "");
        Assert.Null(new CopilotCli().Find($@"{dir}\nothing;{bin}")); // a .cmd is not enough: Process.Start only finds .exe
        File.WriteAllText(Path.Combine(bin, "copilot.exe"), "");
        Assert.Equal(Path.Combine(bin, "copilot.exe"), new CopilotCli().Find($@"{dir}\nothing;{bin}"));
    }

    public void Dispose() => Directory.Delete(dir, true);
}
