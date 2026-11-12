using System.Runtime.InteropServices;

namespace Butler.Tests;

public class WindowsShellTests
{
    [Fact]
    public void Only_an_exe_counts_as_installed()
    {
        var bin = Directory.CreateTempSubdirectory("butler-shell-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(bin, "copilot.cmd"), "");
            Assert.Null(new CopilotCli().Find(new WindowsShell(), bin)); // Process.Start only finds .exe without a shell
        }
        finally { Directory.Delete(bin, true); }
    }

    // A command line as typed in cmd (its quotes kept), the event in !VAR!, which cmd does not parse again.
    [Fact]
    public async Task Command_keeps_its_quotes_and_an_event_value_cannot_add_a_command()
    {
        var home = Directory.CreateTempSubdirectory("butler-cmd-").FullName;
        try
        {
            const string line = "echo \"a b\" & echo !BUTLER_SUBJECT! & echo oops 1>&2 & exit /b 2";
            var psi = new WindowsShell().CommandLine(line);
            foreach (var (k, v) in Trigger.Environment(new Dictionary<string, string> { ["subject"] = "x & echo injected" })) psi.Environment[k] = v;

            var run = await new Runner(home, new ClaudeCli()).RunCommand("x", line, psi);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Equal("oops", run.Summary); // stderr says why
            Assert.Contains("\"a b\"", run.Result);
            Assert.Contains("x & echo injected", run.Result);
            Assert.DoesNotContain("\ninjected", run.Result);
        }
        finally { Directory.Delete(home, true); }
    }

    // A deleted skill goes to the real Recycle Bin, with its subfolders.
    [Fact]
    public void Trash_moves_a_folder_to_the_recycle_bin()
    {
        var folder = Directory.CreateTempSubdirectory("butler-trash-").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), "x");
        File.WriteAllText(Path.Combine(folder, "scripts", "a.py"), "x");

        new WindowsShell().Trash(folder);

        Assert.False(Directory.Exists(folder));
    }

    // The badge on a real window's taskbar button: a count, the dot, none. A wrong call to the taskbar throws.
    // On an STA thread, as Butler's UI thread; the badges drawn first on Avalonia's (the icon tests' session).
    [SkippableFact]
    public void Badge_is_set_on_a_taskbar_button_and_cleared()
    {
        Skip.If(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "The taskbar needs the desktop of a signed-in user");
        string?[] labels = ["3", "9+", "", null];
        AppIconTests.OnUi(() => labels.OfType<string>().Select(AppIcon.BadgePng).ToList());
        Exception? failed = null;
        var sta = new Thread(() =>
        {
            var window = CreateWindowEx(0, "STATIC", "Butler badge test", 0x10CF0000, 0, 0, 200, 100, 0, 0, 0, 0); // overlapped, visible
            try
            {
                Assert.NotEqual(0, window);
                var shell = new WindowsShell();
                foreach (var label in labels) shell.Badge(window, label);
            }
            catch (Exception e) { failed = e; }
            finally { DestroyWindow(window); }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (failed != null) throw failed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
}
