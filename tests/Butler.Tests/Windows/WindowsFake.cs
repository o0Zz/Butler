using System.Diagnostics;

namespace Butler.Tests;

static partial class Fake
{
    public static partial string Cli(string folder) =>
        Write(folder, "claude.cmd", "@echo off\r\necho %* > \"%FAKE_ARGS%\"\r\ntype \"%FAKE_OUT%\"\r\nexit /b %FAKE_EXIT%\r\n");

    public static partial string Hanging(string folder) =>
        Write(folder, "hang.cmd", "@echo off\r\necho {\"type\":\"system\",\"subtype\":\"init\"}\r\nping -n 60 127.0.0.1 > nul\r\n");

    public static partial string Serving(string folder) =>
        Write(folder, "serve.cmd", "@echo off\r\n>> \"%FAKE_ARGS%\" echo %*\r\nset n=0\r\n:loop\r\nset \"line=\"\r\nset /p line=\r\n" +
            "if not defined line exit /b 0\r\nset /a n+=1\r\n>> \"%FAKE_IN%\" echo(%line%\r\n" +
            "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"answer %n%\",\"total_cost_usd\":0.01}\r\ngoto loop\r\n");

    public static partial ProcessStartInfo Exiting() => new("cmd.exe", "/c exit") { CreateNoWindow = true };

    static string Write(string folder, string name, string script)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, script);
        return path;
    }
}
