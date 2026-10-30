using System.Text.Json;

namespace Butler;

// Butler's own settings, local to this machine: <home>/settings.json, edited on the Settings page.
// Every Butler process reads it when it starts, so a change applies to the next run without restarting anything.
public sealed record Settings
{
    static readonly string DefaultSkills = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Cli { get; init; } = "claude";
    public string TriggersFile { get; init; } = Path.Combine(DefaultSkills, "butler.json"); // with the skills it calls, in their git repo
    public string SkillsFolder { get; init; } = DefaultSkills;
    public string AdoOrganization { get; init; } = ""; // for "ado" triggers, e.g. https://dev.azure.com/<org>
    public string Todo { get; init; } = "none"; // the To do page's list: TodoBackend.All (the Todoist token is in Secrets)
    public string TodoFile { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "todo.txt"); // for "txt"
    public string[] TodoHidden { get; init; } = []; // ids of the lists (projects) hidden on the To do page

    // Defaults when the file does not exist. Throws on an unknown CLI or to-do list (the file can be edited by hand).
    public static Settings Load(string path)
    {
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)! : new Settings();
        Butler.Cli.FromName(settings.Cli);
        if (!TodoBackend.All.Any(b => b.Name == settings.Todo)) throw new ArgumentException($"Unknown to-do list '{settings.Todo}'");
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // Why these settings cannot be saved, by field (the names of the Settings page); empty when they can.
    public Dictionary<string, string> Problems()
    {
        var problems = new Dictionary<string, string>();
        try
        {
            if (Butler.Cli.FromName(Cli).Find() == null) problems["cli"] = $"{Cli} is not installed: {Cli}.exe is not on the PATH";
        }
        catch (ArgumentException e) { problems["cli"] = e.Message; }
        if (!File.Exists(TriggersFile)) problems["triggersFile"] = "File not found";
        if (!Directory.Exists(SkillsFolder)) problems["skillsFolder"] = "Folder not found";
        if (AdoOrganization != "" && !AdoOrganization.StartsWith("https://"))
            problems["adoOrganization"] = "The organization URL, like https://dev.azure.com/<org>";
        if (!TodoBackend.All.Any(b => b.Name == Todo)) problems["todo"] = $"Unknown to-do list '{Todo}'";
        if (Todo == "txt" && !Directory.Exists(Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(TodoFile))))
            problems["todoFile"] = "Folder not found (the file itself is created with the first task)";
        return problems;
    }
}
