namespace Butler;

// "hotkey": a key combination pressed in any app (keys: "ctrl+shift+space") runs the prompt on the selected text (${text}).
// The run is quick (Runner.Quick: no tools, no MCP servers; a light model fits) and its answer replaces the selection
// (output "paste", the default: when its window is still in front, else the clipboard) or goes to the clipboard ("clipboard").
// The tray listens (IHotkeys); Poll starts nothing, it only reports keys the system cannot give.
public sealed class HotkeyTrigger : TriggerKind
{
    public static readonly string[] Outputs = ["paste", "clipboard"];

    public override string Name => "hotkey";
    public override string Label => T("Hotkey");
    public override FormField[] Form =>
    [
        new("keys", T("Keys"), "text", T("Like ctrl+shift+space: ctrl, alt, shift or win, and a letter, a digit, F1 to F24 or space. Avoid ctrl+alt+letter on keyboards with AltGr.")),
        new("output", T("Answer"), "text", T("paste: replaces the selection (the clipboard when you switched windows); clipboard: only copied"), Optional: true),
    ];
    public override string[] Variables => ["text"];
    public override string? Context => "The selected text:\n${text}";

    public override void Validate(Trigger t)
    {
        if (t.Command != null) throw new FormatException($"Trigger '{t.Name}': a hotkey trigger runs a prompt, not a command");
        if (Hotkey.Parse(t.Field("keys")) == null)
            throw new FormatException($"Trigger '{t.Name}': keys '{t.Field("keys")}' must be like ctrl+shift+space (ctrl, alt, shift, win, then a letter, digit, F1-F24 or space)");
        if (t.Field("output") is { } o && !Outputs.Contains(o)) throw new FormatException($"Trigger '{t.Name}': output must be paste or clipboard, not '{o}'");
    }

    public override string Describe(Trigger t) => T("When you press {0} on a selected text", Hotkey.Parse(t.Field("keys")));

    public static bool Pastes(Trigger t) => t.Field("output") != "clipboard";

    // The tray listens for the keys itself: nothing to start here, only the keys the system refused to report.
    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir, Platform platform)
    {
        if (platform.Hotkeys.Unavailable is { } why) throw new PlatformNotSupportedException(why);
        if (platform.Hotkeys.Refused.Count > 0) throw new InvalidOperationException(T("Taken by another app: {0}", string.Join(", ", platform.Hotkeys.Refused)));
        return [];
    }
}

// A key combination, written in a normal form: "ctrl+shift+space" (modifiers in this order, lower case).
// Key: a-z, 0-9, f1-f24 or space. At least ctrl, alt or win: shift+letter is typing.
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public static Hotkey? Parse(string? text)
    {
        var parts = (text ?? "").ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        var mods = parts[..^1];
        if (mods.Except(["ctrl", "alt", "shift", "win"]).Any() || mods.Distinct().Count() != mods.Length) return null;
        var key = parts[^1];
        var valid = key is "space" || (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            || (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 24);
        var hotkey = new Hotkey(mods.Contains("ctrl"), mods.Contains("alt"), mods.Contains("shift"), mods.Contains("win"), key);
        return valid && (hotkey.Ctrl || hotkey.Alt || hotkey.Win) ? hotkey : null;
    }

    public override string ToString() =>
        string.Join("+", new[] { Ctrl ? "ctrl" : null, Alt ? "alt" : null, Shift ? "shift" : null, Win ? "win" : null, Key }.OfType<string>());
}
