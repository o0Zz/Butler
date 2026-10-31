using System.Diagnostics;

namespace Butler.Tests;

static partial class Fake
{
    public static partial string Cli(string folder) =>
        Write(folder, "claude", "#!/bin/sh\necho \"$@\" > \"$FAKE_ARGS\"\ncat \"$FAKE_OUT\"\nexit $FAKE_EXIT\n");

    public static partial string Hanging(string folder) =>
        Write(folder, "hang", "#!/bin/sh\necho '{\"type\":\"system\",\"subtype\":\"init\"}'\nsleep 60\n");

    public static partial ProcessStartInfo Exiting() => new("sh", "-c exit");

    static string Write(string folder, string name, string script)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
