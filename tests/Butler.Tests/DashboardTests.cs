using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Butler.Tests;

public sealed class DashboardTests
{
    static string Page([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Butler", "dashboard.html"));

    // An inline handler (oninput="...") looks names up on its element first: a bare `form` there is the element's own
    // form owner (null without a <form>), not a global, and setting it throws. The trigger editor lost its name and prompt so.
    [Fact]
    public void No_inline_handler_uses_a_global_named_form()
    {
        var handlers = Regex.Matches(Page(), @"\bon[a-z]+=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(handlers, h => h.Contains("drawEditor()")); // the search finds the editor's handlers
        Assert.Equal([], handlers.Where(h => Regex.IsMatch(h, @"(?<![.\w])form\b")).ToArray());
    }
}
