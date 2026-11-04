using System.Net.Http.Headers;
using System.Text.Json;

namespace Butler;

// A newer Butler on GitHub, shown under the dashboard's title with a link to its page. Download: this OS's file of it
// (null when the release has none); installed only when the user asks (Install).
public sealed record Release(string Version, string Url, string? Download = null)
{
    public const string Latest = "https://api.github.com/repos/o0Zz/Butler/releases/latest";
    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5), // also downloads the release
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("Butler", "1") } }, // GitHub refuses requests without one
    };

    // The latest release when it is newer than `current` (Program.Version: "1.2.0.57 (abc1234)"), else null.
    // suffix: the end of this OS's file name (IShell.ReleaseSuffix). A development build (0.0.0) has nothing to compare with.
    public static async Task<Release?> Newer(string current, string suffix, string url = Latest)
    {
        if (ThreeParts(current.Split(' ')[0]) is not { } mine || mine == new System.Version(0, 0, 0)) return null;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
        var download = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString()!.EndsWith(suffix, StringComparison.Ordinal))
            .Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault();
        return ThreeParts(tag.TrimStart('v')) is { } latest && latest > mine
            ? new Release(latest.ToString(), doc.RootElement.GetProperty("html_url").GetString()!, download) : null;
    }

    // Puts the download in place of exe: a running exe can be renamed (to exe.old, deleted at the next start), not overwritten.
    // The new file starts as a copy of exe, so it keeps its permissions (executable on Unix).
    public async Task Install(string exe)
    {
        var download = Download ?? throw new InvalidOperationException($"Release {Version} has no file for this system");
        var next = exe + ".new";
        File.Copy(exe, next, overwrite: true);
        await using (var file = new FileStream(next, FileMode.Truncate))
        await using (var body = await Http.GetStreamAsync(download))
            await body.CopyToAsync(file);
        File.Move(exe, exe + ".old", overwrite: true);
        File.Move(next, exe);
    }

    static System.Version? ThreeParts(string text) => System.Version.TryParse(text, out var v) ? new System.Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
}
