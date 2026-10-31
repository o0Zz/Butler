using System.Diagnostics;

namespace Butler.Tests;

static partial class Fake
{
    public static partial string Cli(string folder) =>
        Write(folder, "claude.cmd", "@echo off\r\necho %* > \"%FAKE_ARGS%\"\r\ntype \"%FAKE_OUT%\"\r\nexit /b %FAKE_EXIT%\r\n");

    public static partial string Hanging(string folder) =>
        Write(folder, "hang.cmd", "@echo off\r\necho {\"type\":\"system\",\"subtype\":\"init\"}\r\nping -n 60 127.0.0.1 > nul\r\n");

    public static partial ProcessStartInfo Exiting() => new("cmd.exe", "/c exit") { CreateNoWindow = true };

    static string Write(string folder, string name, string script)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, script);
        return path;
    }
}
