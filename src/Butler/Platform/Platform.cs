using System.Diagnostics;
using Avalonia.Controls;

namespace Butler;

// What differs between operating systems: one implementation of each service per OS. The project compiles
// Platform/Windows for Windows and Platform/Unix for macOS and Linux, each with its own Create.
// Program builds it once and hands it to what needs it.
public sealed partial record Platform(INotifications Notifications, ISecrets Secrets, IStartAtLogin StartAtLogin, IShell Shell, IMailbox Mailbox, IWebView WebView)
{
    public static Platform Current { get; } = Create();

    private static partial Platform Create();
}

// Tells the user how a run ended, or what failed.
public interface INotifications
{
    void Show(RunRecord run);
    void Error(string title, string message);

    // Whether this process was started by a click on a notification: it then only passes the click on.
    bool StartedByClick();
}

// Per-user secrets (the Todoist token), never in settings.json.
public interface ISecrets
{
    const string Todoist = "Butler/Todoist";

    string Name { get; } // where they go, for the Settings page: "the Windows Credential Manager"

    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target); // no error when it is not there
}

// Starting Butler at login. On only when it starts this exe: an entry left by another copy (an older release) reads as off.
public interface IStartAtLogin
{
    string Label { get; } // the Settings page's checkbox: "Start Butler with Windows"
    bool IsOn(string exe);
    void Set(bool on, string exe);
}

// The OS's desktop and command line.
public interface IShell
{
    string ExecutableName(string name); // as found on the PATH: claude.exe on Windows
    string ReleaseSuffix { get; }       // the end of this OS's file in a GitHub release: .exe, -linux-x64, -osx-arm64
    void OpenFolder(string folder);
    void Reveal(string file);           // its folder, with the file selected where the OS can
    void Terminal(string folder, string exe, IEnumerable<string> args); // an interactive program in a terminal window
    ProcessStartInfo Az();              // the az CLI, to add its arguments to
    ProcessStartInfo CommandLine(string line); // a command line run by the OS's shell: cmd on Windows, sh elsewhere
}

// The dashboard's web engine (WebView2, WebKit), set up when the dashboard creates it.
public interface IWebView
{
    void Configure(WebViewEnvironmentRequestedEventArgs engine, string dataFolder); // its profile goes in dataFolder
}

// The mailbox the mail triggers read.
public interface IMailbox
{
    string? Unavailable { get; } // why it cannot be read now, null when it can
    List<Mail> ReadSince(DateTime since); // received at or after since, newest first
}
