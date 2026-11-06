using System.Runtime.InteropServices;

namespace Butler;

// RegisterHotKey on a thread of its own, whose message loop gets WM_HOTKEY. The selection goes through the clipboard:
// Ctrl+C and Ctrl+V sent with SendInput once the hotkey's keys are released (else the app would see Ctrl+Shift+C).
// Only text is put back in the clipboard: when it held something else (an image), it is left with the copied text.
public sealed class WindowsHotkeys : IHotkeys
{
    const int WM_HOTKEY = 0x0312, WM_APP = 0x8000;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    const ushort VK_CONTROL = 0x11;

    readonly object gate = new();
    readonly AutoResetEvent applied = new(false);
    Thread? thread;
    uint threadId;
    IReadOnlyList<Hotkey> wanted = [];
    Action<Hotkey> pressed = _ => { };
    List<Hotkey> refused = [];

    public string? Unavailable => null;
    public IReadOnlyList<Hotkey> Refused { get { lock (gate) return [.. refused]; } }

    public void Listen(IReadOnlyList<Hotkey> keys, Action<Hotkey> pressed)
    {
        lock (gate) (wanted, this.pressed) = (keys, pressed);
        if (thread == null)
        {
            if (keys.Count == 0) return;
            using var ready = new ManualResetEventSlim();
            thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "Butler hotkeys" };
            thread.Start();
            ready.Wait();
        }
        PostThreadMessage(threadId, WM_APP, 0, 0);
        applied.WaitOne(5000); // Refused is up to date on return
    }

    void Loop(ManualResetEventSlim ready)
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0); // creates this thread's message queue before Listen posts to it
        ready.Set();
        IReadOnlyList<Hotkey> registered = [];
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_APP)
            {
                for (var i = 0; i < registered.Count; i++) UnregisterHotKey(0, i + 1);
                lock (gate) registered = wanted;
                var no = registered.Where((k, i) => !RegisterHotKey(0, i + 1, Modifiers(k), VirtualKey(k.Key))).ToList();
                lock (gate) refused = no;
                applied.Set();
            }
            else if (msg.message == WM_HOTKEY && (int)msg.wParam - 1 is var id && id >= 0 && id < registered.Count)
            {
                var key = registered[id];
                Action<Hotkey> act;
                lock (gate) act = pressed;
                Task.Run(() => act(key)); // the loop keeps answering
            }
        }
    }

    public Selection? Copy()
    {
        WaitForKeysReleased();
        var window = GetForegroundWindow();
        var before = ReadText();
        var seq = GetClipboardSequenceNumber();
        Press('C');
        for (var i = 0; i < 50 && GetClipboardSequenceNumber() == seq; i++) Thread.Sleep(20);
        if (GetClipboardSequenceNumber() == seq) return null; // nothing selected: the app copied nothing
        var text = ReadText();
        if (before != null) WriteText(before);
        return string.IsNullOrEmpty(text) ? null : new Selection(text, window, before);
    }

    public bool Paste(Selection selection, string text)
    {
        if (GetForegroundWindow() != selection.Window) return false;
        WaitForKeysReleased();
        WriteText(text);
        Press('V');
        Thread.Sleep(500); // the app reads the clipboard after the keys reach it
        if (selection.Clipboard != null) WriteText(selection.Clipboard);
        return true;
    }

    public void ToClipboard(string text) => WriteText(text);

    static uint Modifiers(Hotkey k) =>
        MOD_NOREPEAT | (k.Ctrl ? MOD_CONTROL : 0) | (k.Alt ? MOD_ALT : 0) | (k.Shift ? MOD_SHIFT : 0) | (k.Win ? MOD_WIN : 0);

    static uint VirtualKey(string key) => key switch
    {
        "space" => 0x20,
        [var c] => char.ToUpperInvariant(c), // A-Z and 0-9 are their ASCII codes
        _ => 0x70 + uint.Parse(key[1..]) - 1, // F1 = 0x70
    };

    // Shift, Ctrl, Alt, Windows: up to 2 s for the user to let go.
    static void WaitForKeysReleased()
    {
        int[] keys = [0x10, 0x11, 0x12, 0x5B, 0x5C];
        for (var i = 0; i < 100 && keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); i++) Thread.Sleep(20);
    }

    // Ctrl + key, as typed.
    static void Press(char key)
    {
        INPUT Key(ushort vk, bool up) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } };
        INPUT[] inputs = [Key(VK_CONTROL, false), Key(key, false), Key(key, true), Key(VK_CONTROL, true)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // Another app may hold the clipboard for a moment. A window of ours owns what we write
    // (with no owner, SetClipboardData fails after EmptyClipboard).
    static T WithClipboard<T>(Func<T> use)
    {
        var owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
        try
        {
            for (var i = 0; i < 25; i++)
            {
                if (OpenClipboard(owner))
                    try { return use(); } finally { CloseClipboard(); }
                Thread.Sleep(20);
            }
            throw new IOException("The clipboard is held by another application");
        }
        finally { DestroyWindow(owner); }
    }

    static string? ReadText() => WithClipboard(() =>
    {
        var data = GetClipboardData(CF_UNICODETEXT);
        if (data == 0) return null;
        var p = GlobalLock(data);
        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(data); }
    });

    static void WriteText(string text) => WithClipboard(() =>
    {
        EmptyClipboard();
        var bytes = (text.Length + 1) * 2;
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
        var p = GlobalLock(memory);
        Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
        Marshal.WriteInt16(p, text.Length * 2, 0);
        GlobalUnlock(memory);
        SetClipboardData(CF_UNICODETEXT, memory); // the clipboard owns it now
        return true;
    });

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] MOUSEINPUT mi; // the largest member: sets the size SendInput expects
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hWnd);
}
