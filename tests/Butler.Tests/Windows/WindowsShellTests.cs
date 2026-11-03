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
}
