using System.Text.RegularExpressions;

namespace Butler;

public sealed record Skill(string Name, string Description, string Folder);

// The user's skills: <dir>/<skill>/SKILL.md, name and description from the front matter.
public static class Skills
{
    public static List<Skill> Load(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var skills = new List<Skill>();
        foreach (var file in Directory.GetDirectories(dir).Select(d => Path.Combine(d, "SKILL.md")).Where(File.Exists))
        {
            var front = Regex.Match(File.ReadAllText(file), @"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
            string? Field(string key) => Regex.Match(front.Groups[1].Value, $@"^{key}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            var folder = Path.GetFileName(Path.GetDirectoryName(file)!);
            skills.Add(new(Field("name") ?? folder, Field("description") ?? "", folder));
        }
        return [.. skills.OrderBy(s => s.Name)];
    }

    // "/triage-mail do X" -> "triage-mail"; null when the prompt does not start with a slash command.
    public static string? Invoked(string prompt) =>
        Regex.Match(prompt, @"^/([\w:-]+)") is { Success: true } m ? m.Groups[1].Value : null;
}
