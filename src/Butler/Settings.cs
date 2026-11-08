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
    public string StartPage { get; init; } = "runs"; // the dashboard's page when it opens: one of Pages
    public string Language { get; init; } = "auto"; // of what Butler shows: "auto" (the OS's) or one of Lang.All
    public string BackupFolder { get; init; } = ""; // where Backup writes its daily zip; empty: no backup
    public static readonly string[] Pages = ["runs", "todo", "triggers", "skills", "settings"];

    // A trigger's model is a level (Levels), turned into a model name of the CLI here (CLI -> level -> name; empty: the CLI's default),
    // so the triggers do not change with the CLI. Any other value is a model name, passed as is.
    public static readonly string[] Levels = ["fast", "balanced", "powerful"];
    public Dictionary<string, Dictionary<string, string>> Models { get; init; } = new()
    {
        ["claude"] = new() { ["fast"] = "haiku", ["balanced"] = "sonnet", ["powerful"] = "opus" },
        ["copilot"] = new() { ["fast"] = "", ["balanced"] = "", ["powerful"] = "" },
    };

    // The --model of a trigger's run with the CLI of these settings; null: the CLI's default.
    public string? ModelFor(string? model) => string.IsNullOrWhiteSpace(model) ? null
        : !Levels.Contains(model) ? model
        : Models.GetValueOrDefault(Cli)?.GetValueOrDefault(model) is { Length: > 0 } name ? name.Trim() : null;

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
    public Dictionary<string, string> Problems(IShell shell)
    {
        var problems = new Dictionary<string, string>();
        try
        {
            if (Butler.Cli.FromName(Cli).Find(shell) == null) problems["cli"] = T("{0} is not installed: {1} is not on the PATH", Cli, shell.ExecutableName(Cli));
        }
        catch (ArgumentException e) { problems["cli"] = e.Message; }
        if (!File.Exists(TriggersFile) && !Directory.Exists(Path.GetDirectoryName(TriggersFile)))
            problems["triggersFile"] = T("Folder not found (the file itself is created empty)");
        if (!Directory.Exists(SkillsFolder)) problems["skillsFolder"] = T("Folder not found");
        if (AdoOrganization != "" && !AdoOrganization.StartsWith("https://"))
            problems["adoOrganization"] = T("The organization URL, like https://dev.azure.com/<org>");
        if (!Pages.Contains(StartPage)) problems["startPage"] = T("Unknown page '{0}'", StartPage);
        if (!TodoBackend.All.Any(b => b.Name == Todo)) problems["todo"] = T("Unknown to-do list '{0}'", Todo);
        if (Todo == "txt" && !Directory.Exists(Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(TodoFile))))
            problems["todoFile"] = T("Folder not found (the file itself is created with the first task)");
        if (Language != "auto" && !Lang.All.Contains(Language)) problems["language"] = T("Unknown language '{0}'", Language);
        if (BackupFolder != "")
        {
            var backup = Path.GetFullPath(Environment.ExpandEnvironmentVariables(BackupFolder));
            if (!Directory.Exists(backup) && !Directory.Exists(Path.GetDirectoryName(backup))) problems["backupFolder"] = T("Folder not found");
            else if (SkillsFolder != "" && Backup.Inside(backup, SkillsFolder)) problems["backupFolder"] = T("Not in the skills folder: each backup would contain the previous ones");
        }
        return problems;
    }
}
