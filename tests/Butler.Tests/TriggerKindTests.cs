using System.Text.Json;

namespace Butler.Tests;

public class TriggerKindTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-kinds-").FullName;
    string State => Path.Combine(dir, "state");

    List<Trigger> LoadJson(string json)
    {
        var path = Path.Combine(dir, "butler.json");
        File.WriteAllText(path, json);
        return Trigger.Load(path);
    }

    [Fact]
    public void Loads_ado_and_file_triggers_with_their_own_fields()
    {
        var triggers = LoadJson("""
            [
              { "name": "mine", "on": "ado", "where": "[System.AssignedTo] = @Me", "prompt": "/x #${id} ${title}" },
              { "name": "specs", "on": "file", "folder": "%TEMP%", "filter": "*.docx", "prompt": "/fmea ${path}" }
            ]
            """);
        Assert.Equal("[System.AssignedTo] = @Me", triggers[0].Field("WHERE"));
        Assert.Equal("When a work item changes where [System.AssignedTo] = @Me", triggers[0].Kind.Describe(triggers[0]));
        Assert.Equal("When a file (*.docx) appears in %TEMP%", triggers[1].Kind.Describe(triggers[1]));
        Assert.Equal("/fmea C:\\in\\spec.docx", triggers[1].PromptWith(new Dictionary<string, string> { ["path"] = @"C:\in\spec.docx" }));
    }

    [Theory]
    [InlineData("""[{ "name": "x", "on": "file", "prompt": "p" }]""", "folder is missing")]
    [InlineData("""[{ "name": "x", "on": "file", "folder": "C:\\x", "filter": "C:\\x\\*.pdf", "prompt": "p" }]""", "not a path")]
    [InlineData("""[{ "name": "x", "on": "file", "folder": "C:\\x", "prompt": "/x ${from}" }]""", "use ${path}, ${name}")]
    [InlineData("""[{ "name": "x", "on": "ado", "prompt": "p" }]""", "where is missing")]
    [InlineData("""[{ "name": "x", "on": "mail", "from": ["a"], "folder": "C:\\x", "prompt": "p" }]""", "unknown field 'folder' for an 'mail' trigger")]
    [InlineData("""[{ "name": "x", "on": "teams", "prompt": "p" }]""", "on must be 'schedule', 'mail', 'ado', 'file'")]
    public void Rejects_wrong_fields(string json, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => LoadJson(json)).Message);

    static Trigger FileTrigger(string folder, string? filter = null) => new("drop", "file", "/x")
    {
        Fields = new() { ["folder"] = JsonSerializer.SerializeToElement(folder), ["filter"] = JsonSerializer.SerializeToElement(filter ?? "*") },
    };

    static string Write(string folder, string name, int ageSeconds)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTime(path, DateTime.Now.AddSeconds(-ageSeconds));
        return path;
    }

    [Fact]
    public void Schedule_trigger_runs_once_per_time_passed_however_many_were_missed()
    {
        var kind = new ScheduleTrigger();
        var brief = new Trigger("brief", "schedule", "/brief", Schedule: "mon-fri 07:30");
        int Runs(Trigger t, string at) => kind.Poll([t], State, DateTime.Parse(at)).Count;

        Assert.Equal(0, Runs(brief, "2026-11-02 08:00")); // Monday, first poll: today's time is already gone
        Assert.Equal(0, Runs(brief, "2026-11-03 07:29"));
        Assert.Equal(1, Runs(brief, "2026-11-03 07:30"));
        Assert.Equal(0, Runs(brief, "2026-11-03 07:31")); // never twice
        Assert.Equal(1, Runs(brief, "2026-11-06 10:00")); // PC off from Tuesday to Friday: one run, not three
        Assert.Equal(0, Runs(brief, "2026-11-07 10:00")); // Saturday
        Assert.Equal(1, Runs(brief, "2026-11-09 07:30"));

        var moved = brief with { Schedule = "daily 09:00" }; // a changed schedule starts from now
        Assert.Equal(0, Runs(moved, "2026-11-09 10:00"));
        Assert.Equal(1, Runs(moved, "2026-11-10 09:00"));
    }

    [Fact]
    public void File_trigger_fires_once_per_new_file_once_it_is_written()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(dir, "inbox")).FullName;
        Write(inbox, "old.pdf", 3600);
        var kind = new FileTrigger();
        var t = FileTrigger(inbox, "*.pdf");
        var poll = () => kind.Poll([t], new Settings(), State).Select(r => r.Values["name"]).ToList();

        Assert.Empty(poll()); // first poll: what is already there is not replayed
        var spec = Write(inbox, "spec.pdf", 60);
        Write(inbox, "downloading.pdf", 0); // still being written
        Write(inbox, "notes.txt", 60);      // filtered out

        var runs = kind.Poll([t], new Settings(), State);
        Assert.Equal(["spec.pdf"], runs.Select(r => r.Values["name"]));
        Assert.Equal(spec, runs[0].Values["path"]);
        Assert.Empty(poll()); // never twice

        File.SetLastWriteTime(Path.Combine(inbox, "downloading.pdf"), DateTime.Now.AddSeconds(-60));
        Assert.Equal(["downloading.pdf"], poll());
    }

    [Fact]
    public void File_trigger_on_a_missing_folder_fails_and_forgets_nothing()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(dir, "inbox")).FullName;
        var kind = new FileTrigger();
        var t = FileTrigger(inbox);
        kind.Poll([t], new Settings(), State);
        Write(inbox, "a.pdf", 60);

        Assert.Throws<DirectoryNotFoundException>(() => kind.Poll([t, FileTrigger(Path.Combine(dir, "nope")) with { Name = "other" }], new Settings(), State));
        Assert.Equal(["a.pdf"], kind.Poll([t], new Settings(), State).Select(r => r.Values["name"]));
    }

    [Fact]
    public void Ado_trigger_needs_the_organization_setting()
    {
        var t = LoadJson("""[{ "name": "mine", "on": "ado", "where": "[System.AssignedTo] = @Me", "prompt": "/x" }]""");
        Assert.Contains("organization", Assert.Throws<InvalidOperationException>(() => new AdoTrigger().Poll(t, new Settings(), State)).Message);
    }

    // Real Azure DevOps through az (logged in): set BUTLER_TEST_ADO_ORG to the organization URL.
    [SkippableFact]
    public void Ado_query_reads_work_items_changed_since_a_date()
    {
        var org = Environment.GetEnvironmentVariable("BUTLER_TEST_ADO_ORG");
        Skip.If(string.IsNullOrEmpty(org), "BUTLER_TEST_ADO_ORG is not set");
        var since = DateTime.Now.AddDays(-30);

        var items = AdoTrigger.Query(org!, "[System.AssignedTo] = @Me", since);

        Assert.All(items, w => Assert.True(w.Changed >= since.Date.AddDays(-1) && w.Id > 0 && w.Title.Length > 0 && w.ChangedBy.Length > 0));
        var t = LoadJson("""[{ "name": "mine", "on": "ado", "where": "[System.AssignedTo] = @Me", "prompt": "/x" }]""");
        Assert.Empty(new AdoTrigger().Poll(t, new Settings { AdoOrganization = org! }, State)); // first poll starts from now
    }

    public void Dispose() => Directory.Delete(dir, true);
}
