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
        Assert.EndsWith(Path.Combine(".claude", "skills", "butler.json"), s.TriggersFile);
        Assert.EndsWith(Path.Combine(".claude", "skills"), s.SkillsFolder);
    }

    [Fact]
    public void Saved_settings_are_read_back_from_a_camel_case_file()
    {
        new Settings { Cli = "copilot", TriggersFile = @"C:\t\triggers.json", SkillsFolder = @"C:\t" }.Save(File_);
        Assert.Contains("\"triggersFile\"", File.ReadAllText(File_));
        Assert.Equivalent(new Settings { Cli = "copilot", TriggersFile = @"C:\t\triggers.json", SkillsFolder = @"C:\t" }, Settings.Load(File_), strict: true);

        File.WriteAllText(File_, """{ "cli": "copilot" }"""); // edited by hand: what is missing keeps its default
        Assert.Equivalent(new Settings { Cli = "copilot" }, Settings.Load(File_), strict: true);
    }

    [Fact]
    public void A_level_is_the_model_set_for_the_cli_and_any_other_value_a_model_name()
    {
        var claude = new Settings();
        Assert.Equal(("haiku", "sonnet", "opus"), (claude.ModelFor("light"), claude.ModelFor("mid"), claude.ModelFor("strong")));
        Assert.Equal("claude-fable-5-1", claude.ModelFor("claude-fable-5-1")); // a name, as is
        Assert.Null(claude.ModelFor(null)); // the CLI's default
        Assert.Null(claude.ModelFor(" "));

        var copilot = new Settings { Cli = "copilot" };
        Assert.Null(copilot.ModelFor("strong")); // not set for copilot: its default

        File.WriteAllText(File_, """{ "cli": "claude", "models": { "claude": { "light": "sonnet", "strong": "claude-fable-5-1" }, "copilot": { "light": "gpt-5" } } }""");
        var mine = Settings.Load(File_);
        Assert.Equal(("sonnet", null, "claude-fable-5-1"), (mine.ModelFor("light"), mine.ModelFor("mid"), mine.ModelFor("strong")));
        Assert.Equal("gpt-5", (mine with { Cli = "copilot" }).ModelFor("light"));
        mine.Save(File_);
        Assert.Equal("claude-fable-5-1", Settings.Load(File_).Models["claude"]["strong"]);
    }

    [Fact]
    public void Hidden_todo_lists_are_saved_and_kept_by_other_changes()
    {
        new Settings { TodoHidden = ["2203306141", "Home"] }.Save(File_);
        var changed = Settings.Load(File_) with { Cli = "copilot" }; // as the Settings page saves
        changed.Save(File_);
        Assert.Equal(["2203306141", "Home"], Settings.Load(File_).TodoHidden);
    }

    [Fact]
    public void The_dashboard_starts_on_runs_by_default_and_an_unknown_page_is_refused()
    {
        Assert.Equal("runs", Settings.Load(File_).StartPage);
        File.WriteAllText(File_, """{ "startPage": "todo" }""");
        Assert.Equal("todo", Settings.Load(File_).StartPage);
        Assert.DoesNotContain("startPage", Settings.Load(File_).Problems(Platform.Current.Shell).Keys);
        Assert.Equal("Unknown page 'inbox'", new Settings { StartPage = "inbox" }.Problems(Platform.Current.Shell)["startPage"]);
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

        var bad = new Settings { Cli = "gpt", TriggersFile = Path.Combine(dir, "nope.json"), SkillsFolder = Path.Combine(dir, "nope") }.Problems(Platform.Current.Shell);
        Assert.Equal(["cli", "skillsFolder", "triggersFile"], bad.Keys.Order());

        var good = new Settings { Cli = "claude", TriggersFile = triggers, SkillsFolder = dir }.Problems(Platform.Current.Shell);
        Assert.DoesNotContain("triggersFile", good.Keys);
        Assert.DoesNotContain("skillsFolder", good.Keys);
    }

    [Fact]
    public void The_todo_list_is_none_by_default_and_an_unknown_one_is_refused()
    {
        Assert.Equal("none", Settings.Load(File_).Todo);
        File.WriteAllText(File_, """{ "todo": "trello" }""");
        Assert.Throws<ArgumentException>(() => Settings.Load(File_));
        Assert.Equal("Unknown to-do list 'trello'", new Settings { Todo = "trello" }.Problems(Platform.Current.Shell)["todo"]);
    }

    [Fact]
    public void A_todo_txt_file_may_not_exist_yet_but_its_folder_must()
    {
        Assert.DoesNotContain("todoFile", new Settings { Todo = "txt", TodoFile = Path.Combine(dir, "todo.txt") }.Problems(Platform.Current.Shell).Keys);
        Assert.Contains("todoFile", new Settings { Todo = "txt", TodoFile = Path.Combine(dir, "nope", "todo.txt") }.Problems(Platform.Current.Shell).Keys);
        Assert.DoesNotContain("todoFile", new Settings { Todo = "none", TodoFile = Path.Combine(dir, "nope", "todo.txt") }.Problems(Platform.Current.Shell).Keys);
    }

    [Fact]
    public void A_cli_is_installed_when_its_exe_is_on_the_path()
    {
        var shell = Platform.Current.Shell;
        var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
        var path = string.Join(Path.PathSeparator, Path.Combine(dir, "nothing"), bin);
        Assert.Null(new CopilotCli().Find(shell, path));
        File.WriteAllText(Path.Combine(bin, shell.ExecutableName("copilot")), "");
        Assert.Equal(Path.Combine(bin, shell.ExecutableName("copilot")), new CopilotCli().Find(shell, path));
    }

    public void Dispose() => Directory.Delete(dir, true);
}
