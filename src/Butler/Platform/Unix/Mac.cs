using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Avalonia.Controls;

namespace Butler;

// Notification Center, through osascript. The texts go as arguments, never inside the script.
sealed class OsascriptNotifications : PlainNotifications
{
    protected override void Notify(string title, string message) =>
        Command.Run("osascript", ["-e", "on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run", title, message]);
}

// The login keychain, through the security tool. Stored in base64: security prints a non-ASCII secret in hex, which
// cannot be told apart from a hex token.
sealed class Keychain : ISecrets
{
    public string Name => T("the macOS Keychain");

    public string? Read(string target) =>
        Command.Run("security", ["find-generic-password", "-s", target, "-w"]) is (0, var output, _)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(output.Trim())) : null;

    public void Write(string target, string secret) => // -U: replaces it
        Command.Check("security", ["add-generic-password", "-U", "-s", target, "-a", Environment.UserName, "-w",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(secret))]);

    public void Delete(string target) => Command.Run("security", ["delete-generic-password", "-s", target]); // fails when not there
}

// A LaunchAgent in ~/Library/LaunchAgents, run at login.
sealed class LaunchAgent(string name = "Butler") : StartupFile
{
    protected override string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"local.{name}.plist");

    protected override string Content(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>local.{SecurityElement.Escape(name)}</string>
          <key>ProgramArguments</key><array><string>{SecurityElement.Escape(exe)}</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """;
}

// Finder and Terminal.
sealed class MacShell : IShell
{
    public string ExecutableName(string name) => name;
    public string ReleaseSuffix => "-osx-arm64";
    public void OpenFolder(string folder) => Command.Start("open", [folder]);
    public void Reveal(string file) => Command.Start("open", ["-R", file]);
    public void Trash(string folder) =>
        Command.Check("osascript", ["-e", "on run argv\ntell application \"Finder\" to delete (POSIX file (item 1 of argv))\nend run", folder]);

    // The Dock icon's badge (one per app, whatever the window), on the main thread: [NSApp.dockTile setBadgeLabel:].
    public void Badge(nint window, string? label)
    {
        var tile = Send(Send(Class("NSApplication"), "sharedApplication"), "dockTile");
        var text = label == null ? 0 : Send(Class("NSString"), "stringWithUTF8String:", label == "" ? "!" : label);
        Send(tile, "setBadgeLabel:", text);
    }

    // The label shown (null for none), read back the same way.
    public static string? BadgeLabel() =>
        Marshal.PtrToStringUTF8(Send(Send(Send(Send(Class("NSApplication"), "sharedApplication"), "dockTile"), "badgeLabel"), "UTF8String"));

    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")] static extern nint Class(string name);
    [DllImport(ObjC)] static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint target, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint target, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);
    static nint Send(nint target, string selector) => target == 0 ? 0 : Send(target, sel_registerName(selector));
    static nint Send(nint target, string selector, nint arg) => Send(target, sel_registerName(selector), arg);
    static nint Send(nint target, string selector, string arg) => Send(target, sel_registerName(selector), arg);

    public void Terminal(string folder, string exe, IEnumerable<string> args) =>
        Command.Start("osascript", ["-e", "on run argv\ntell application \"Terminal\" to do script (item 1 of argv)\nend run", Command.ShellLine(folder, exe, args)]);

    public ProcessStartInfo Az() => new("az");
    public ProcessStartInfo CommandLine(string line) => new("/bin/sh") { ArgumentList = { "-c", line } };
}

// WKWebView: its data stays in the app's own WebKit store, which has no folder to choose.
sealed class WkWebView : IWebView
{
    public void Configure(WebViewEnvironmentRequestedEventArgs engine, string dataFolder) { }
}

// Mail triggers read Outlook classic, which only exists on Windows.
sealed class NoMailbox : IMailbox
{
    public string? Unavailable => T("Mail triggers read Outlook classic, which only runs on Windows");
    public List<Mail> ReadSince(DateTime since) => throw new InvalidOperationException(Unavailable);
}
