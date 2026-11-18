using System.Text.RegularExpressions;

namespace Butler;

// Folder: the folder's name in the skills folder; for a plugin's skill, its full path (the CLI's copy, not the user's).
public sealed record Skill(string Name, string Description, string Folder, string? Plugin = null);

// The user's skills: <dir>/<skill>/SKILL.md, name and description from the front matter.
// Then the plugins' (Cli.Plugins): <folder>/<skill>/SKILL.md, called /<plugin>:<name>.
public static class Skills
{
    public static List<Skill> Load(string dir, IEnumerable<(string Name, string Folder)>? plugins = null) =>
        [.. In(dir, null).Concat((plugins ?? []).SelectMany(p => In(p.Folder, p.Name))).OrderBy(s => s.Name)];

    static IEnumerable<Skill> In(string dir, string? plugin)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetDirectories(dir).Select(d => Path.Combine(d, "SKILL.md")).Where(File.Exists))
        {
            var front = Regex.Match(File.ReadAllText(file), @"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
            string? Field(string key) => Regex.Match(front.Groups[1].Value, $@"^{key}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            var folder = Path.GetDirectoryName(file)!;
            var name = Field("name") ?? Path.GetFileName(folder);
            yield return plugin == null ? new(name, Field("description") ?? "", Path.GetFileName(folder)) : new($"{plugin}:{name}", Field("description") ?? "", folder, plugin);
        }
    }

    // The folder of the skill named by its folder name: only one of `dir`'s skills (no "..", no path), else null.
    public static string? FolderOf(string dir, string folder) =>
        Load(dir).Any(s => s.Folder == folder) ? Path.Combine(dir, folder) : null;

    // "/triage-mail do X" -> "triage-mail"; null when the prompt does not start with a slash command.
    public static string? Invoked(string prompt) =>
        Regex.Match(prompt, @"^/([\w:-]+)") is { Success: true } m ? m.Groups[1].Value : null;
}
