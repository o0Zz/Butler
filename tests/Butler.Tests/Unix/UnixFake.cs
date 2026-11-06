using System.Diagnostics;

namespace Butler.Tests;

static partial class Fake
{
    public static partial string Cli(string folder) =>
        Write(folder, "claude", "#!/bin/sh\necho \"$@\" > \"$FAKE_ARGS\"\ncat \"$FAKE_OUT\"\nexit $FAKE_EXIT\n");

    public static partial string Hanging(string folder) =>
        Write(folder, "hang", "#!/bin/sh\necho '{\"type\":\"system\",\"subtype\":\"init\"}'\nsleep 60\n");

    public static partial string Serving(string folder) =>
        Write(folder, "serve", "#!/bin/sh\necho \"$@\" >> \"$FAKE_ARGS\"\nn=0\nwhile IFS= read -r line; do\n  n=$((n+1))\n" +
            "  printf '%s\\n' \"$line\" >> \"$FAKE_IN\"\n" +
            "  echo \"{\\\"type\\\":\\\"result\\\",\\\"is_error\\\":false,\\\"result\\\":\\\"answer $n\\\",\\\"total_cost_usd\\\":0.01}\"\ndone\n");

    public static partial ProcessStartInfo Exiting() => new("sh", "-c exit");

    static string Write(string folder, string name, string script)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, script);
        Command.Check("chmod", ["+x", path]);
        return path;
    }
}
