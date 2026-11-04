using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Butler;

// The desktop's notifications, through notify-send (libnotify-bin).
sealed class NotifySend : PlainNotifications
{
    protected override void Notify(string title, string message)
    {
        try { Command.Run("notify-send", ["--app-name=Butler", title, message]); }
        catch (Win32Exception) { } // not installed: no notification, the run still counts
    }
}

// The desktop's secret service (GNOME Keyring, KWallet), through secret-tool (libsecret-tools). The secret goes on stdin.
sealed class SecretTool : ISecrets
{
    public string Name => T("the desktop's secret service");

    public string? Read(string target) =>
        Command.Run("secret-tool", ["lookup", "service", "Butler", "target", target]) is (0, var output, _) ? output : null;

    public void Write(string target, string secret) =>
        Command.Check("secret-tool", ["store", "--label", target, "service", "Butler", "target", target], input: secret);

    public void Delete(string target) => Command.Run("secret-tool", ["clear", "service", "Butler", "target", target]);
}

// An XDG autostart entry (~/.config/autostart), started by the desktop at login.
sealed class AutostartFile(string name = "Butler") : StartupFile
{
    protected override string File => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", $"{name}.desktop");

    protected override string Content(string exe) => $"[Desktop Entry]\nType=Application\nName={name}\nExec=\"{exe}\"\n";
}

// WPE WebKit when installed, else WebKitGTK: either way, its data and cache in the folder.
sealed class WebKitLinux : IWebView
{
    public void Configure(WebViewEnvironmentRequestedEventArgs engine, string dataFolder)
    {
        switch (engine)
        {
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                (wpe.DataDirectory, wpe.CacheDirectory) = (dataFolder, Path.Combine(dataFolder, "cache"));
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                (gtk.BaseDataDirectory, gtk.BaseCacheDirectory) = (dataFolder, Path.Combine(dataFolder, "cache"));
                break;
        }
    }
}

// The file manager (xdg-open) and the default terminal (x-terminal-emulator, Debian and Ubuntu).
sealed class LinuxShell : IShell
{
    public string ExecutableName(string name) => name;
    public string ReleaseSuffix => "-linux-x64";
    public void OpenFolder(string folder) => Command.Start("xdg-open", [folder]);
    public void Reveal(string file) => OpenFolder(Path.GetDirectoryName(file)!); // no standard way to select it

    public void Terminal(string folder, string exe, IEnumerable<string> args) =>
        Command.Start("x-terminal-emulator", ["-e", "sh", "-c", Command.ShellLine(folder, exe, args)]);

    public ProcessStartInfo Az() => new("az");
    public ProcessStartInfo CommandLine(string line) => new("/bin/sh") { ArgumentList = { "-c", line } };
}
