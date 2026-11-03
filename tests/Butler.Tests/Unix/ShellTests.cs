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
}
