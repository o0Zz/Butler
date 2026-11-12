using System.Diagnostics;
using System.Runtime.InteropServices;

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

    // The taskbar's overlay icon, on the UI thread (the taskbar's COM object is single-threaded).
    public void Badge(nint window, string? label)
    {
        var taskbar = (ITaskbarList4)new TaskbarList();
        try
        {
            taskbar.HrInit();
            if (label == null) { taskbar.SetOverlayIcon(window, 0, null); return; }
            var png = AppIcon.BadgePng(label);
            var icon = CreateIconFromResourceEx(png, png.Length, true, 0x00030000, 0, 0, 0);
            if (icon == 0) throw new System.ComponentModel.Win32Exception();
            try { taskbar.SetOverlayIcon(window, icon, label == "" ? "Butler" : label); }
            finally { DestroyIcon(icon); } // the taskbar keeps its own copy
        }
        finally { Marshal.ReleaseComObject(taskbar); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint CreateIconFromResourceEx(byte[] bits, int size, bool icon, int version, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarList;

    // ITaskbarList4 (ITaskbarList3 with one more method at the end): only SetOverlayIcon is called; the methods before it keep the vtable's order.
    [ComImport, Guid("c43dc798-95d1-4bea-9030-bb99e2983a1a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList4
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, int flags);
        void RegisterTab(nint tab, nint mdi);
        void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint insertBefore);
        void SetTabActive(nint tab, nint mdi, uint reserved);
        void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        void ThumbBarSetImageList(nint hwnd, nint images);
        void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

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
