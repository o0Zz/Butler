namespace Butler.Tests;

public class ShellTests
{
    // A command line for sh, the event in "$VAR", which sh does not parse again.
    [Fact]
    public async Task Command_runs_in_sh_and_an_event_value_cannot_add_a_command()
    {
        var home = Directory.CreateTempSubdirectory("butler-sh-").FullName;
        try
        {
            const string line = "echo \"a b\"; echo \"$BUTLER_SUBJECT\"; echo oops >&2; exit 2";
            var psi = Platform.Current.Shell.CommandLine(line);
            foreach (var (k, v) in Trigger.Environment(new Dictionary<string, string> { ["subject"] = "x; echo injected $(echo sub)" })) psi.Environment[k] = v;

            var run = await new Runner(home, new ClaudeCli()).RunCommand("x", line, psi);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Equal("oops", run.Summary); // stderr says why
            Assert.Contains("a b", run.Result);
            Assert.Contains("x; echo injected $(echo sub)", run.Result);
        }
        finally { Directory.Delete(home, true); }
    }

    // A deleted skill goes to the real Trash (gio on Linux, Finder on macOS), with its subfolders.
    [SkippableFact]
    public void Trash_moves_a_folder_to_the_trash()
    {
        Skip.If(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Finder needs a signed-in user");
        Skip.If(OperatingSystem.IsLinux() && Command.Run("sh", ["-c", "command -v gio"]).Exit != 0, "gio is not installed");
        var folder = Directory.CreateTempSubdirectory("butler-trash-").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "scripts", "a.py"), "x");
        try
        {
            Platform.Current.Shell.Trash(folder);
            Assert.False(Directory.Exists(folder));
        }
        catch (InvalidOperationException e) when (OperatingSystem.IsLinux())
        {
            Directory.Delete(folder, true);
            Skip.If(true, $"No trash here: {e.Message}"); // a CI runner's /tmp may have no trash
        }
    }

    // The Dock icon's badge, read back from the Dock: a count, "!" for the dot, none.
    [SkippableFact]
    public void Badge_is_set_on_the_dock_icon_and_cleared()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Linux has no standard taskbar badge");
        var shell = new MacShell();
        shell.Badge(0, "3");
        Assert.Equal("3", MacShell.BadgeLabel());
        shell.Badge(0, "");
        Assert.Equal("!", MacShell.BadgeLabel());
        shell.Badge(0, null);
        Assert.Null(MacShell.BadgeLabel());
    }
}
