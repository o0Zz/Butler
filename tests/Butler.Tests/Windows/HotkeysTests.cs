using System.Runtime.InteropServices;

namespace Butler.Tests;

// Real system-wide hotkeys: registered with Windows, pressed with keybd_event. Ctrl+Alt+Shift+F23: nobody's keys.
public sealed class HotkeysTests
{
    static readonly Hotkey Key = Hotkey.Parse("ctrl+alt+shift+f23")!;

    [SkippableFact]
    public void A_pressed_hotkey_is_reported_and_one_taken_is_refused_until_released()
    {
        Skip.If(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Pressing keys needs the desktop of a signed-in user");
        var hotkeys = new WindowsHotkeys();
        var other = new WindowsHotkeys();
        using var pressed = new SemaphoreSlim(0);
        Hotkey? got = null;
        try
        {
            hotkeys.Listen([Key], k => { got = k; pressed.Release(); });
            Assert.Empty(hotkeys.Refused);
            other.Listen([Key], _ => { });
            Assert.Equal([Key], other.Refused); // already taken (by the first)

            Press(0x11, 0x12, 0x10, 0x86); // Ctrl, Alt, Shift, F23
            Assert.True(pressed.Wait(5000));
            Assert.Equal(Key, got);

            hotkeys.Listen([], _ => { }); // released: another app can have it
            other.Listen([Key], _ => { });
            Assert.Empty(other.Refused);
        }
        finally
        {
            hotkeys.Listen([], _ => { });
            other.Listen([], _ => { });
        }
    }

    static void Press(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, 0);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, 2 /* KEYEVENTF_KEYUP */, 0);
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);
}
