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
        [new("where", T("Work items where"), "text", T("A WIQL condition, e.g. [System.AssignedTo] = @Me AND [System.ChangedBy] <> @Me"))];
    public override string Help =>
        "`where`: a WIQL condition; a run per work item created or changed that matches it, checked every 2 min with the az CLI " +
        "on the organization of Butler's settings (`adoOrganization`). Add `[System.ChangedBy] <> @Me` to skip the user's own changes.";
    public override string[] Variables => ["id", "title", "state", "by"];
    public override string? Context => "Triggered by this Azure DevOps work item change: #${id} \"${title}\" (${state}), changed by ${by}.";
    public override TimeSpan Interval => TimeSpan.FromMinutes(2); // az takes seconds per call

    public override void Validate(Trigger t)
    {
        if (string.IsNullOrWhiteSpace(t.Field("where"))) throw new FormatException($"Trigger '{t.Name}': where is missing (a WIQL condition)");
    }

    public override string Describe(Trigger t) => T("When a work item changes where {0}", t.Field("where"));

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir, Platform platform)
    {
        if (string.IsNullOrWhiteSpace(settings.AdoOrganization)) throw new InvalidOperationException("Set the Azure DevOps organization in Settings");
        var path = Path.Combine(stateDir, "ado.json");
        var old = Checkpoint.Load<Dictionary<string, Checkpoint>>(path);
        string Key(Trigger t) => $"{t.Name}|{t.Field("where")}"; // a new condition starts from now
        var checkpoints = triggers.Select(Key).Distinct().ToDictionary(k => k, k => old.GetValueOrDefault(k) ?? new Checkpoint()); // forgets removed triggers
        // All queries first: a poll that fails moves no checkpoint, the next one sees the same changes.
        var results = triggers.Select(t => (t, items: Query(platform.Shell, settings.AdoOrganization, t.Field("where")!, checkpoints[Key(t)].Last))).ToList();
        var runs = results.SelectMany(r => checkpoints[Key(r.t)].TakeNew(r.items, w => w.Id.ToString(), w => w.Changed)
            .Select(w => (r.t, new Dictionary<string, string> { ["id"] = w.Id.ToString(), ["title"] = w.Title, ["state"] = w.State, ["by"] = w.ChangedBy })))
            .ToList();
        Checkpoint.Save(path, checkpoints);
        return runs;
    }

    // The work items matching the condition changed at or after `since` (local time), through `az boards query`.
    public static List<WorkItem> Query(IShell shell, string organization, string where, DateTime since)
    {
        // WIQL compares dates without the time: take from the day before, the exact filter is the checkpoint's.
        var wiql = "SELECT [System.Id], [System.Title], [System.State], [System.ChangedDate], [System.ChangedBy] FROM workitems " +
                   $"WHERE ({where}) AND [System.ChangedDate] >= '{since.AddDays(-1):yyyy-MM-dd}'";
        var psi = shell.Az();
        (psi.UseShellExecute, psi.CreateNoWindow, psi.RedirectStandardOutput, psi.RedirectStandardError) = (false, true, true, true);
        (psi.StandardOutputEncoding, psi.StandardErrorEncoding) = (Encoding.UTF8, Encoding.UTF8);
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; // az writes cp1252 otherwise
        foreach (var a in new[] { "boards", "query", "--wiql", wiql, "--org", organization, "-o", "json" }) psi.ArgumentList.Add(a);
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

}
