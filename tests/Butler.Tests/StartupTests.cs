using Microsoft.Win32;

namespace Butler.Tests;

// Against the user's real Run key, under a throwaway name.
public class StartupTests
{
    [Fact]
    public void Start_with_windows_is_turned_on_and_off_in_the_run_key()
    {
        var name = $"ButlerTest-{Guid.NewGuid()}";
        var startup = new RunKeyStartup(name);
        const string exe = @"C:\Program Files\Butler\Butler.exe";
        string? Value()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(name) as string;
        }
        try
        {
            Assert.False(startup.IsOn(exe));
            startup.Set(true, exe);
            Assert.Equal("\"C:\\Program Files\\Butler\\Butler.exe\"", Value()); // quoted: the path has spaces
            Assert.True(startup.IsOn(exe));
            Assert.False(startup.IsOn(@"C:\old\Butler-0.9.0.exe")); // another copy starts: off for this one
            startup.Set(false, exe);
            Assert.Null(Value());
            startup.Set(false, exe); // already off: no error
        }
        finally { startup.Set(false, exe); }
    }
}
