using System.Globalization;
using System.Text.Json;

namespace Butler;

// The language of what Butler shows (the `language` setting). The English text is the key: Lang/<code>.json (embedded)
// maps it to its translation, and text without one stays English. The dashboard gets the same map (`tr` in the page).
// Set once per process from the settings (Use), so the texts are read where they are shown, without passing it around.
public static class Lang
{
    public static readonly string[] All = ["en", "fr"]; // "auto": the OS's language when it is one of them, else English

    public static string Code { get; private set; } = "en";
    public static IReadOnlyDictionary<string, string> Texts { get; private set; } = new Dictionary<string, string>();
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Code);

    public static void Use(string language)
    {
        var os = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Code = All.Contains(language) ? language : All.Contains(os) ? os : "en";
        Texts = Load(Code);
    }

    public static Dictionary<string, string> Load(string code)
    {
        using var s = typeof(Lang).Assembly.GetManifestResourceStream($"Butler.Lang.{code}.json");
        return s == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
    }

    // The text in the current language; with args, "{0}" and the like are filled.
    public static string T(string english, params object?[] args)
    {
        var text = Texts.GetValueOrDefault(english, english);
        return args.Length == 0 ? text : string.Format(Culture, text, args);
    }
}
