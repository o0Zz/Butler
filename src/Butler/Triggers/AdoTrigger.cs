using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Butler;

public sealed record WorkItem(int Id, string Title, string State, DateTime Changed, string ChangedBy);

// "ado": an Azure DevOps work item matching a WIQL condition ("where") was created or changed, e.g.
// "[System.AssignedTo] = @Me AND [System.ChangedBy] <> @Me". Read with the az CLI (logged in, azure-devops extension)
// on the organization of the settings. Checkpoint: <state>/ado.json, one per trigger and condition.
public sealed class AdoTrigger : TriggerKind
{
    public override string Name => "ado";
    public override string Label => "Azure DevOps";
    public override FormField[] Form =>
        [new("where", "Work items where", "text", "A WIQL condition, e.g. [System.AssignedTo] = @Me AND [System.ChangedBy] <> @Me")];
    public override string[] Variables => ["id", "title", "state", "by"];
    public override string? Context => "Triggered by this Azure DevOps work item change: #${id} \"${title}\" (${state}), changed by ${by}.";
    public override TimeSpan Interval => TimeSpan.FromMinutes(2); // az takes seconds per call

    public override void Validate(Trigger t)
    {
        if (string.IsNullOrWhiteSpace(t.Field("where"))) throw new FormatException($"Trigger '{t.Name}': where is missing (a WIQL condition)");
    }

    public override string Describe(Trigger t) => $"When a work item changes where {t.Field("where")}";

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir)
    {
        if (string.IsNullOrWhiteSpace(settings.AdoOrganization)) throw new InvalidOperationException("Set the Azure DevOps organization in Settings");
        var path = Path.Combine(stateDir, "ado.json");
        var old = Checkpoint.Load<Dictionary<string, Checkpoint>>(path);
        string Key(Trigger t) => $"{t.Name}|{t.Field("where")}"; // a new condition starts from now
        var checkpoints = triggers.Select(Key).Distinct().ToDictionary(k => k, k => old.GetValueOrDefault(k) ?? new Checkpoint()); // forgets removed triggers
        // All queries first: a poll that fails moves no checkpoint, the next one sees the same changes.
        var results = triggers.Select(t => (t, items: Query(settings.AdoOrganization, t.Field("where")!, checkpoints[Key(t)].Last))).ToList();
        var runs = results.SelectMany(r => checkpoints[Key(r.t)].TakeNew(r.items, w => w.Id.ToString(), w => w.Changed)
            .Select(w => (r.t, new Dictionary<string, string> { ["id"] = w.Id.ToString(), ["title"] = w.Title, ["state"] = w.State, ["by"] = w.ChangedBy })))
            .ToList();
        Checkpoint.Save(path, checkpoints);
        return runs;
    }

    // The work items matching the condition changed at or after `since` (local time), through `az boards query`.
    public static List<WorkItem> Query(string organization, string where, DateTime since)
    {
        // WIQL compares dates without the time: take from the day before, the exact filter is the checkpoint's.
        var wiql = "SELECT [System.Id], [System.Title], [System.State], [System.ChangedDate], [System.ChangedBy] FROM workitems " +
                   $"WHERE ({where}) AND [System.ChangedDate] >= '{since.AddDays(-1):yyyy-MM-dd}'";
        var psi = new ProcessStartInfo(AzPython())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Environment = { ["PYTHONIOENCODING"] = "utf-8", ["AZ_INSTALLER"] = "MSI" }, // utf-8: az writes cp1252 otherwise
        };
        foreach (var a in new[] { "-IBm", "azure.cli", "boards", "query", "--wiql", wiql, "--org", organization, "-o", "json" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("az boards query failed: " + err.Result.Trim());

        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(item =>
        {
            var f = item.GetProperty("fields");
            var by = f.TryGetProperty("System.ChangedBy", out var b) ? b.ValueKind == JsonValueKind.Object ? b.GetProperty("displayName").GetString() : b.GetString() : null;
            return new WorkItem(f.GetProperty("System.Id").GetInt32(), f.GetProperty("System.Title").GetString() ?? "",
                f.GetProperty("System.State").GetString() ?? "", f.GetProperty("System.ChangedDate").GetDateTime().ToLocalTime(), by ?? "");
        })];
    }

    // az.cmd runs "..\python.exe -IBm azure.cli %*" through cmd.exe, which re-parses the arguments: the quotes of a WIQL
    // get lost and its "<" / ">" become redirections. So Butler starts that python itself, with each argument intact.
    static string AzPython()
    {
        var cmd = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "az.cmd")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The az CLI is not installed (az.cmd is not on the PATH)");
        var python = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmd)!, "..", "python.exe"));
        return File.Exists(python) ? python : throw new FileNotFoundException($"The az CLI has no {python}: reinstall it with the MSI");
    }
}
