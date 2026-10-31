using Microsoft.Win32;

namespace Butler.Tests;

// Against the user's real Run key, under a throwaway name.
public class StartupTests
{
    [Fact]
    public void Start_with_windows_is_turned_on_and_off_in_the_run_key()
    {
        var name = $"ButlerTest-{Guid.NewGuid()}";
        const string exe = @"C:\Program Files\Butler\Butler.exe";
        string? Value()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(name) as string;
        }
        try
        {
            Assert.False(Startup.IsOn(exe, name));
            Startup.Set(true, exe, name);
            Assert.Equal("\"C:\\Program Files\\Butler\\Butler.exe\"", Value()); // quoted: the path has spaces
            Assert.True(Startup.IsOn(exe, name));
            Assert.False(Startup.IsOn(@"C:\old\Butler-0.9.0.exe", name)); // another copy starts: off for this one
            Startup.Set(false, exe, name);
            Assert.Null(Value());
            Startup.Set(false, exe, name); // already off: no error
        }
        finally { Startup.Set(false, exe, name); }
    }
}
