using Microsoft.Win32;

namespace Butler;

// "Start with Windows": a value of the user's Run key (also listed in Task Manager's startup apps). The key is the
// setting, not settings.json, so it cannot disagree with what Windows starts.
public static class Startup
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // On only when it starts this exe: a value left by another copy (an older release) reads as off.
    public static bool IsOn(string exe, string name = "Butler")
    {
        using var key = Registry.CurrentUser.OpenSubKey(Run);
        return key?.GetValue(name) as string == Command(exe);
    }

    public static void Set(bool on, string exe, string name = "Butler")
    {
        using var key = Registry.CurrentUser.CreateSubKey(Run);
        if (on) key.SetValue(name, Command(exe));
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    static string Command(string exe) => $"\"{exe}\"";
}
