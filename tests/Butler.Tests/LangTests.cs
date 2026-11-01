using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Butler.Tests;

// The language is set for the whole process: these tests run alone, and leave it in English.
[CollectionDefinition("Lang", DisableParallelization = true)]
public sealed class LangCollection;

[Collection("Lang")]
public sealed class LangTests : IDisposable
{
    public void Dispose() => Lang.Use("en");

    static string Source([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Butler");

    // The texts of the page (tr('...'), data-tr) and of the C# code (T("...")).
    static IEnumerable<string> Used()
    {
        var page = File.ReadAllText(Path.Combine(Source(), "dashboard.html"));
        var js = Regex.Matches(page, @"\btr\('((?:[^'\\]|\\.)*)'|\btr\(""((?:[^""\\]|\\.)*)""|data-tr>([^<]*)<|data-tr-title title=""([^""]*)""")
            .Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value.Replace("\\'", "'"));
        var cs = Directory.EnumerateFiles(Source(), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\bT\(""((?:[^""\\]|\\.)*)""").Select(m => Regex.Unescape(m.Groups[1].Value)));
        return js.Concat(cs).Distinct();
    }

    [Fact]
    public void Every_text_has_its_French_translation()
    {
        var fr = Lang.Load("fr");
        var used = Used().ToList();
        Assert.Contains("Every day at {0}", used); // the search finds them
        Assert.Contains("Open the triggers file in your editor", used);
        Assert.Equal([], used.Where(t => !fr.ContainsKey(t)).ToArray()); // lists the missing ones
    }

    [Fact]
    public void A_translation_keeps_the_placeholders()
    {
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
        Assert.Empty(Lang.Load("fr").Where(kv => Holes(kv.Key) != Holes(kv.Value)).Select(kv => kv.Key));
    }

    [Fact]
    public void In_French_the_texts_are_French()
    {
        Lang.Use("fr");
        var schedule = new ScheduleTrigger();
        Assert.Equal("Tous les jours à 09:00", schedule.Describe(new Trigger("x", "schedule", "/x", Schedule: "daily 09:00")));
        Assert.Equal("Chaque lundi, mercredi à 09:00", schedule.Describe(new Trigger("x", "schedule", "/x", Schedule: "mon,wed 09:00")));
        Assert.Equal("Planification", schedule.Label);
        Assert.Equal("Page inconnue 'inbox'", new Settings { StartPage = "inbox" }.Problems(Platform.Current.Shell)["startPage"]);

        Lang.Use("en");
        Assert.Equal("Every Monday, Wednesday at 09:00", schedule.Describe(new Trigger("x", "schedule", "/x", Schedule: "mon,wed 09:00")));
        Assert.Equal("Untranslated", Lang.T("Untranslated")); // no translation: the English text
    }

    [Fact]
    public void Auto_is_the_OS_language_when_Butler_has_it()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            Lang.Use("auto");
            Assert.Equal("fr", Lang.Code);
            Lang.Use("en");
            Assert.Equal("en", Lang.Code); // chosen over the OS's
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Lang.Use("auto");
            Assert.Equal("en", Lang.Code);
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    [Fact]
    public void An_unknown_language_is_refused()
    {
        Assert.Equal("Unknown language 'xx'", new Settings { Language = "xx" }.Problems(Platform.Current.Shell)["language"]);
        Assert.DoesNotContain("language", new Settings { Language = "fr" }.Problems(Platform.Current.Shell).Keys);
        Assert.DoesNotContain("language", new Settings().Problems(Platform.Current.Shell).Keys); // "auto"
    }
}
