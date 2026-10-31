using System.Diagnostics;

namespace Butler.Tests;

// Fake commands for the runner: .cmd scripts on Windows, sh scripts elsewhere (Windows/, Unix/).
static partial class Fake
{
    // A CLI that writes its arguments to %FAKE_ARGS%, prints the file %FAKE_OUT% and exits with %FAKE_EXIT%.
    public static partial string Cli(string folder);

    // A CLI that prints its init line and then hangs for a minute.
    public static partial string Hanging(string folder);

    // A process that ends at once.
    public static partial ProcessStartInfo Exiting();
}
