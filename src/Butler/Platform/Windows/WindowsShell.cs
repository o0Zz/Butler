using System.Diagnostics;

namespace Butler;

// Explorer, Windows Terminal, and az through its own python.
public sealed class WindowsShell : IShell
{
    public string ExecutableName(string name) => name + ".exe"; // all Process.Start finds without a shell
    public string ReleaseSuffix => ".exe";

    public void OpenFolder(string folder) => Process.Start("explorer.exe", folder);

    public void Reveal(string file) => Process.Start("explorer.exe", $"/select,\"{file}\"");

    public void Trash(string folder) => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public void Terminal(string folder, string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder, exe } };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    // As typed in a cmd window: /s strips the outer quotes and keeps the line as is (ArgumentList would escape its quotes as \").
    // /v:on for !VAR!, expanded after the line is parsed: a %VAR% holding "& del ..." would run it.
    public ProcessStartInfo CommandLine(string line) => new("cmd.exe") { Arguments = $"/d /v:on /s /c \"{line}\"" };

    // az.cmd runs "..\python.exe -IBm azure.cli %*" through cmd.exe, which re-parses the arguments: the quotes of a WIQL
    // get lost and its "<" / ">" become redirections. So Butler starts that python itself, with each argument intact.
    public ProcessStartInfo Az()
    {
        var cmd = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "az.cmd")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The az CLI is not installed (az.cmd is not on the PATH)");
        var python = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmd)!, "..", "python.exe"));
        if (!File.Exists(python)) throw new FileNotFoundException($"The az CLI has no {python}: reinstall it with the MSI");
        return new ProcessStartInfo(python) { ArgumentList = { "-IBm", "azure.cli" }, Environment = { ["AZ_INSTALLER"] = "MSI" } };
    }
}
