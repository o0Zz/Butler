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
}
