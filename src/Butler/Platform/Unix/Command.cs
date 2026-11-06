using System.ComponentModel;
using System.Diagnostics;

namespace Butler;

// The macOS and Linux services are command line tools: run one and read what it says.
static class Command
{
    public static (int Exit, string Output, string Error) Run(string file, IEnumerable<string> args, string? input = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (input != null)
        {
            p.StandardInput.Write(input);
            p.StandardInput.Close();
        }
        var error = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error.Result.Trim());
    }

    // Runs it and throws with what it said when it fails.
    public static void Check(string file, IEnumerable<string> args, string? input = null)
    {
        if (Run(file, args, input) is (not 0, _, var error)) throw new InvalidOperationException($"{file} failed: {error}");
    }

    // Starts it without waiting (a file manager, a terminal).
    public static void Start(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    // "cd 'folder' && 'exe' 'arg' ...": a command line for sh, every part quoted.
    public static string ShellLine(string folder, string exe, IEnumerable<string> args) =>
        $"cd {Quote(folder)} && {string.Join(' ', args.Prepend(exe).Select(Quote))}";

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

// macOS and Linux: a plain notification, without buttons: a waiting run is answered on the dashboard.
// Hotkeys need an Accessibility permission on macOS to read and replace a selection, and Wayland has no global keys.
sealed class NoHotkeys : IHotkeys
{
    public string? Unavailable => T("Hotkey triggers only work on Windows for now");
    public IReadOnlyList<Hotkey> Refused => [];
    public void Listen(IReadOnlyList<Hotkey> keys, Action<Hotkey> pressed) { }
    public Selection? Copy() => null;
    public bool Paste(Selection selection, string text) => false;
    public void ToClipboard(string text) { }
}

abstract class PlainNotifications : INotifications
{
    public void Show(RunRecord run) => Notify($"{run.Trigger}: {T(run.Status.ToString())}", run.Question ?? run.Summary ?? "");
    public void Error(string title, string message) => Notify(title, message);
    public bool StartedByClick() => false;

    protected abstract void Notify(string title, string message);
}

// Starting at login through a file the desktop reads (a LaunchAgent, an autostart entry): on when it holds this exe.
abstract class StartupFile : IStartAtLogin
{
    public string Label => T("Start Butler at login");
    protected abstract string File { get; }
    protected abstract string Content(string exe);

    public bool IsOn(string exe) => System.IO.File.Exists(File) && System.IO.File.ReadAllText(File) == Content(exe);

    public void Set(bool on, string exe)
    {
        if (!on)
        {
            System.IO.File.Delete(File);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, Content(exe));
    }
}
