namespace Butler.Tests;

// The command lines Butler hands to a terminal (macOS Terminal, x-terminal-emulator), run by the real sh.
public class CommandTests
{
    [Fact]
    public void A_shell_line_keeps_every_argument_as_it_is()
    {
        var dir = Directory.CreateTempSubdirectory("butler cmd 'x'").FullName;
        try
        {
            var line = Command.ShellLine(dir, "printf", ["%s|", "two words", "it's", "$HOME", "a;b"]);
            Assert.Equal((0, "two words|it's|$HOME|a;b|", ""), Command.Run("sh", ["-c", line]));
            Assert.Equal(dir, Command.Run("sh", ["-c", Command.ShellLine(dir, "pwd", [])]).Output.Trim());
        }
        finally { Directory.Delete(dir, true); }
    }
}
