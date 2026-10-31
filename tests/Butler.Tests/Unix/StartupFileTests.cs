namespace Butler.Tests;

// The real autostart folder (Linux) or LaunchAgents folder (macOS), under a throwaway name.
public class StartupFileTests
{
    [Fact]
    public void Start_at_login_is_turned_on_and_off()
    {
        var name = $"ButlerTest-{Guid.NewGuid()}";
        IStartAtLogin startup = OperatingSystem.IsMacOS() ? new LaunchAgent(name) : new AutostartFile(name);
        const string exe = "/opt/Butler Apps/Butler";
        try
        {
            Assert.False(startup.IsOn(exe));
            startup.Set(true, exe);
            Assert.True(startup.IsOn(exe));
            Assert.False(startup.IsOn("/old/Butler-0.9.0")); // another copy starts: off for this one
            startup.Set(false, exe);
            Assert.False(startup.IsOn(exe));
            startup.Set(false, exe); // already off: no error
        }
        finally { startup.Set(false, exe); }
    }
}
