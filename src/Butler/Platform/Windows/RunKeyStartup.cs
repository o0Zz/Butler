using Microsoft.Win32;

namespace Butler;

// "Start with Windows": a value of the user's Run key (also listed in Task Manager's startup apps). The key is the
// setting, not settings.json, so it cannot disagree with what Windows starts.
public sealed class RunKeyStartup(string name = "Butler") : IStartAtLogin
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string Label => T("Start Butler with Windows");

    public bool IsOn(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Run);
        return key?.GetValue(name) as string == Command(exe);
    }

    public void Set(bool on, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Run);
        if (on) key.SetValue(name, Command(exe));
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    static string Command(string exe) => $"\"{exe}\"";
}
