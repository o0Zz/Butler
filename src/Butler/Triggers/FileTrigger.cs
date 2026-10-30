using System.Text.Json;

namespace Butler;

// "file": a new file in a folder (not its subfolders), matching an optional filter ("*.pdf"). The folder can use %VARIABLES%.
// Checkpoint: <state>/file.json, the files already seen per trigger. A file counts once it has not changed for 10 s
// (a download or a copy still being written is picked at a later poll).
public sealed class FileTrigger : TriggerKind
{
    public override string Name => "file";
    public override string Label => "New file";
    public override FormField[] Form =>
        [new("folder", "Folder", "folder", "Not its subfolders; %VARIABLES% allowed"), new("filter", "Filter", "text", "A file pattern like *.pdf", Optional: true)];
    public override string[] Variables => ["path", "name"];
    public override string? Context => "Triggered by this new file: ${path}";

    public override void Validate(Trigger t)
    {
        if (string.IsNullOrWhiteSpace(t.Field("folder"))) throw new FormatException($"Trigger '{t.Name}': folder is missing");
        if (t.Field("filter") is { } f && f.IndexOfAny(['\\', '/']) >= 0) throw new FormatException($"Trigger '{t.Name}': filter is a file pattern like *.pdf, not a path");
    }

    public override string Describe(Trigger t) => $"When a file{(t.Field("filter") is { } f ? $" ({f})" : "")} appears in {t.Field("folder")}";

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir)
    {
        // A poll that fails moves no checkpoint.
        var missing = triggers.Select(Folder).Where(f => !Directory.Exists(f)).Distinct().ToList();
        if (missing.Count > 0) throw new DirectoryNotFoundException($"Folder not found: {string.Join(", ", missing)}");

        var path = Path.Combine(stateDir, "file.json");
        var old = Checkpoint.Load<Dictionary<string, List<string>>>(path);
        var seen = new Dictionary<string, List<string>>(); // only the current triggers: removed ones are forgotten
        var runs = new List<(Trigger, Dictionary<string, string>)>();
        foreach (var t in triggers)
        {
            var folder = Folder(t);
            var key = $"{t.Name}|{folder}|{t.Field("filter")}"; // a new folder or filter starts from now, it does not replay the folder
            var files = Directory.GetFiles(folder, t.Field("filter") ?? "*");
            if (!old.TryGetValue(key, out var known))
            {
                seen[key] = [.. files.Select(f => Path.GetFileName(f))];
                continue;
            }
            var knownSet = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stableBefore = DateTime.Now.AddSeconds(-10);
            var fresh = files.Where(f => !knownSet.Contains(Path.GetFileName(f)) && File.GetLastWriteTime(f) < stableBefore).ToList();
            runs.AddRange(fresh.Select(f => (t, new Dictionary<string, string> { ["path"] = f, ["name"] = Path.GetFileName(f) })));
            seen[key] = [.. files.Select(f => Path.GetFileName(f)).Where(knownSet.Contains).Concat(fresh.Select(f => Path.GetFileName(f)))];
        }
        Checkpoint.Save(path, seen);
        return runs;
    }

    static string Folder(Trigger t) => Environment.ExpandEnvironmentVariables(t.Field("folder")!);
}
