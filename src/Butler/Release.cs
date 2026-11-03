using System.Net.Http.Headers;
using System.Text.Json;

namespace Butler;

// A newer Butler on GitHub, shown under the dashboard's title with a link to its page. Never downloaded nor installed.
public sealed record Release(string Version, string Url)
{
    public const string Latest = "https://api.github.com/repos/o0Zz/Butler/releases/latest";
    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("Butler", "1") } }, // GitHub refuses requests without one
    };

    // The latest release when it is newer than `current` (Program.Version: "1.2.0.57 (abc1234)"), else null.
    // A development build (0.0.0) has nothing to compare with.
    public static async Task<Release?> Newer(string current, string url = Latest)
    {
        if (ThreeParts(current.Split(' ')[0]) is not { } mine || mine == new System.Version(0, 0, 0)) return null;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
        return ThreeParts(tag.TrimStart('v')) is { } latest && latest > mine
            ? new Release(latest.ToString(), doc.RootElement.GetProperty("html_url").GetString()!) : null;
    }

    static System.Version? ThreeParts(string text) => System.Version.TryParse(text, out var v) ? new System.Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
}
