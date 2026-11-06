using System.Diagnostics;
using System.Text.Json;

namespace Butler;

public enum RunStatus { Running, Done, Failed, Waiting, Answered, Stopped }

// runs/<Id>.json; the raw claude stream goes to runs/<Id>.log.
public sealed class RunRecord
{
    public required string Id { get; init; }
    public required string Trigger { get; init; }
    public required string SessionId { get; init; }
    public required string Prompt { get; init; }
    public string? Cli { get; init; }   // the CLI that owns the session (null: runs from before the CLI setting, claude)
    public string? Model { get; init; } // its --model (null: the CLI's default); an answer continues with it
    public DateTime Started { get; init; }
    public DateTime? Ended { get; set; }
    public RunStatus Status { get; set; }
    public string? Summary { get; set; }
    public string? Question { get; set; }
    public string? Result { get; set; }
    public decimal? CostUsd { get; set; }
    public int? Pid { get; init; }      // the process that runs it and records the outcome: if it is gone, the run is dead
    public int? ClaudePid { get; set; } // the CLI process: what Stop kills
}

// Runs the CLI (claude or copilot) hidden, records the run in <home>/runs, and reports the outcome.
public sealed class Runner(string home, Cli cli)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public const string CommandCli = "command"; // the Cli of a command trigger's runs: the OS's shell, no agent

    public string RunsDir { get; } = Path.Combine(home, "runs");
    public Cli Cli { get; } = cli;

    public static string Protocol(string trigger) =>
        $"You are running unattended, launched by Butler (trigger: {trigger}). Nobody can answer you during this run. " +
        "If you need a decision or a confirmation from the user before going further, or you leave the user something to do " +
        "(a draft to send, a page to finish), stop there and end your final message " +
        "with one line: `BUTLER-ASK: <short question>`. The user will answer later in this same session. " +
        "Otherwise end your final message with one line: `BUTLER-SUMMARY: <one-line outcome>`.";

    public const string QuickProtocol =
        "Your answer replaces the text the user selected, as is: give only the resulting text, " +
        "without any introduction, comment, quotes or code fences.";

    public Task<RunRecord> Run(string trigger, string prompt, string? model = null, string? resumeSession = null)
    {
        var session = resumeSession ?? Guid.NewGuid().ToString();
        return Agent(trigger, prompt, session, model, Cli.RunArguments(prompt, Protocol(trigger), session, resumeSession != null, model), quick: false);
    }

    // A hotkey trigger's run: no tools nor MCP servers (seconds, not tens of them), the answer is the result as is.
    // Done unless the CLI failed; never waiting. Started for this prompt only (Run now, or a CLI that cannot stay running).
    public Task<RunRecord> Quick(string trigger, string prompt, string? model, bool mine) =>
        Agent(trigger, prompt, Guid.NewGuid().ToString(), model, Cli.QuickArguments(prompt, QuickProtocol, model, mine), quick: true,
            QuickAgent.Folder(mine, home));

    // The same through the CLI kept running (QuickAgent): about a second instead of four. Recorded like any run.
    public async Task<RunRecord> Quick(string trigger, string prompt, QuickAgent agent)
    {
        var run = Start(trigger, prompt, Guid.NewGuid().ToString(), Cli.Name, agent.Model);
        CliResult? answer = null;
        string? error = null;
        using (var log = new StreamWriter(Path.Combine(RunsDir, run.Id + ".log")))
            try { answer = await agent.Ask(prompt, log.WriteLine); }
            catch (Exception e) when (e is TimeoutException or IOException or System.ComponentModel.Win32Exception) { error = e.Message; }
        run.CostUsd = answer?.CostUsd;
        return Finish(run, () => QuickOutcome(run, answer?.Result ?? error, answer is not { IsError: false }));
    }

    // The answer as is; failed when the CLI did or said nothing.
    static void QuickOutcome(RunRecord run, string? result, bool failed)
    {
        run.Result = result;
        if (failed || string.IsNullOrWhiteSpace(result)) Interpret(run, isError: true);
        else (run.Status, run.Summary, run.Result) = (RunStatus.Done, Short(result, 120), result.Trim());
    }

    async Task<RunRecord> Agent(string trigger, string prompt, string sessionId, string? model, IEnumerable<string> arguments, bool quick, string? folder = null)
    {
        var run = Start(trigger, prompt, sessionId, Cli.Name, model);
        var psi = new ProcessStartInfo(Cli.Exe) { WorkingDirectory = folder ?? "" };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        string? result = null, lastText = null;
        bool isError = false;
        var (ok, err) = await Execute(run, psi, line =>
        {
            foreach (var e in Events(Cli, line))
            {
                if (e is CliText t) lastText = t.Text;
                else if (e is CliResult r) (result, isError, run.CostUsd) = (r.Result, r.IsError, r.CostUsd);
            }
        });
        result ??= lastText; // a CLI without a final result event (copilot) ends with its last message
        if (result == null && err.Length > 0) result = err.Trim();
        return Finish(run, () =>
        {
            if (quick) QuickOutcome(run, result, isError || !ok);
            else
            {
                run.Result = result;
                Interpret(run, isError || !ok);
            }
        });
    }

    // A command trigger: the command line in the OS's shell (IShell.CommandLine), no agent. Done when it exits with 0,
    // its last output line as the summary; never waiting for an answer.
    public async Task<RunRecord> RunCommand(string trigger, string command, ProcessStartInfo shell)
    {
        var run = Start(trigger, command, Guid.NewGuid().ToString(), CommandCli, null);
        var output = new List<string>();
        var (ok, err) = await Execute(run, shell, output.Add);
        static string? Last(IEnumerable<string> lines) => lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        var all = string.Join("\n", output.Append(err.TrimEnd())).Trim();
        return Finish(run, () =>
        {
            run.Status = ok ? RunStatus.Done : RunStatus.Failed;
            run.Summary = ok ? Last(output) ?? "" : Last(err.Split('\n')) ?? Last(output) ?? T("the command failed");
            // As preformatted text; the whole output is in the log.
            run.Result = all.Length == 0 ? null : "````\n" + (all.Length > 4000 ? "…" + all[^4000..] : all) + "\n````";
        });
    }

    RunRecord Start(string trigger, string prompt, string sessionId, string cli, string? model)
    {
        Directory.CreateDirectory(RunsDir);
        var run = new RunRecord
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{trigger}", // sortable: Recent() orders by file name
            Trigger = trigger,
            SessionId = sessionId,
            Prompt = prompt,
            Cli = cli,
            Model = model,
            Started = DateTime.Now,
            Status = RunStatus.Running,
            Pid = Environment.ProcessId,
        };
        Save(run);
        return run;
    }

    // Runs the process hidden, its output line by line to the log and to `read`. Ok when it exited with 0;
    // its stderr, or why it could not start.
    async Task<(bool Ok, string Error)> Execute(RunRecord run, ProcessStartInfo psi, Action<string> read)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8; // default is the ANSI code page: "é" came out as "Ã©"
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        // Not the Butler repo: its CLAUDE.md is about developing Butler, not about the triggered task.
        if (psi.WorkingDirectory == "") psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            using var log = new StreamWriter(Path.Combine(RunsDir, run.Id + ".log"));
            using var p = Process.Start(psi)!;
            run.ClaudePid = p.Id;
            Save(run);
            var stderr = p.StandardError.ReadToEndAsync();
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                await log.WriteLineAsync(line);
                await log.FlushAsync();
                read(line);
            }
            await p.WaitForExitAsync();
            var err = await stderr;
            if (err.Length > 0) await log.WriteLineAsync("[stderr] " + err);
            return (p.ExitCode == 0, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // Records the outcome, unless Stop killed the process meanwhile: its outcome stays.
    RunRecord Finish(RunRecord run, Action outcome)
    {
        if (Load(run.Id) is { Status: RunStatus.Stopped } stopped) return stopped;
        run.Ended = DateTime.Now;
        outcome();
        Save(run);
        return run;
    }

    // Marks the waiting run as answered and resumes its session with the answer.
    public Task<RunRecord> Answer(string runId, string answer)
    {
        var waiting = Load(runId) ?? throw new ArgumentException($"Unknown run {runId}");
        if (waiting.Status != RunStatus.Waiting) throw new InvalidOperationException($"Run {runId} is not waiting for an answer");
        if ((waiting.Cli ?? "claude") != Cli.Name)
            throw new InvalidOperationException($"Run {runId} is a {waiting.Cli ?? "claude"} session and the CLI setting is {Cli.Name}: switch back to answer it");
        waiting.Status = RunStatus.Answered;
        waiting.Summary = T("Answered: {0}", answer);
        Save(waiting);
        return Run(waiting.Trigger, answer, waiting.Model, waiting.SessionId);
    }

    // Kills claude and what it started (the log is kept). A waiting run is only dismissed.
    public RunRecord Stop(string runId)
    {
        var run = Load(runId) ?? throw new ArgumentException($"Unknown run {runId}");
        if (run.Status is not (RunStatus.Running or RunStatus.Waiting)) return run;
        var claude = run.Status == RunStatus.Running ? Live(run.ClaudePid, p => p.StartTime >= run.Started.AddSeconds(-2)) : null;
        run.Status = RunStatus.Stopped;
        run.Ended = DateTime.Now;
        run.Summary = T("Stopped from the dashboard");
        Save(run); // before the kill, so the run process sees it and keeps it
        if (claude != null)
        {
            claude.Kill(entireProcessTree: true);
            claude.WaitForExit(5000);
        }
        return run;
    }

    // Deletes every run (record and log) but those still running or waiting for an answer.
    public void Clear()
    {
        foreach (var run in Recent(int.MaxValue).Where(r => r.Status is not (RunStatus.Running or RunStatus.Waiting)))
        {
            File.Delete(Path.Combine(RunsDir, run.Id + ".json"));
            File.Delete(Path.Combine(RunsDir, run.Id + ".log"));
        }
    }

    // The process with that id, if it is still running and passes the check (ids get reused).
    static Process? Live(int? pid, Func<Process, bool> isOurs)
    {
        if (pid is not { } id) return null;
        try
        {
            var p = Process.GetProcessById(id);
            return !p.HasExited && isOurs(p) ? p : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // A run still "Running" whose process is gone (killed, sign-out, reboot) will never be finished: say so.
    void MarkIfDead(RunRecord run)
    {
        if (run.Status != RunStatus.Running || Live(run.Pid, p => p.StartTime <= run.Started.AddSeconds(2)) != null) return;
        var log = Path.Combine(RunsDir, run.Id + ".log");
        run.Status = RunStatus.Stopped;
        run.Ended = File.Exists(log) ? File.GetLastWriteTime(log) : run.Started;
        run.Summary = T("Stopped: the Butler process running it ended before Claude finished (killed, sign-out or reboot)");
        Save(run);
    }

    internal static void Interpret(RunRecord run, bool isError)
    {
        var lines = (run.Result ?? "").Split('\n').Select(l => l.Trim().Trim('`')).ToList();
        var ask = lines.LastOrDefault(l => l.StartsWith("BUTLER-ASK:"));
        var summary = lines.LastOrDefault(l => l.StartsWith("BUTLER-SUMMARY:"));
        if (isError)
        {
            run.Status = RunStatus.Failed;
            run.Summary = lines.FirstOrDefault(l => l.Length > 0) ?? T("the run failed");
        }
        else if (ask != null)
        {
            run.Status = RunStatus.Waiting;
            run.Question = ask["BUTLER-ASK:".Length..].Trim();
        }
        else
        {
            run.Status = RunStatus.Done;
            run.Summary = summary?["BUTLER-SUMMARY:".Length..].Trim() ?? lines.FirstOrDefault(l => l.Length > 0) ?? "";
        }
    }

    // What the agent said and which tools it used, one short line each, read from the raw stream (also while it runs).
    public List<string> Activity(string runId)
    {
        var lines = new List<string>();
        var path = Path.Combine(RunsDir, runId + ".log");
        if (!File.Exists(path)) return lines;
        var run = Load(runId);
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        if (run?.Cli == CommandCli) // its output, while it runs (then it is the result)
        {
            while (run.Status == RunStatus.Running && reader.ReadLine() is { } line) lines.Add(Short(line, 300));
            return lines;
        }
        var cli = Cli.FromName(run?.Cli); // the log is in the format of the CLI that wrote it
        while (reader.ReadLine() is { } line)
            foreach (var e in Events(cli, line))
            {
                if (e is CliText t) lines.Add(Short(t.Text.Replace("BUTLER-ASK:", "").Replace("BUTLER-SUMMARY:", "").Trim(), 300));
                else if (e is CliTool tool) lines.Add($"▸ {ToolName(tool.Name)}{ToolDetail(tool.Input)}");
            }
        return lines;
    }

    internal static List<CliEvent> Events(Cli cli, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return [.. cli.Read(doc.RootElement)];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return []; } // partial last line
    }

    // "mcp__claude-in-chrome__navigate" -> "claude-in-chrome navigate"
    static string ToolName(string name) => name.StartsWith("mcp__") ? name[5..].Replace("__", " ") : name;

    static string ToolDetail(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "description", "command", "file_path", "pattern", "query", "url", "skill", "prompt" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return ": " + Short(v.GetString()!, 160);
        return "";
    }

    static string Short(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "…";
    }

    public RunRecord? Load(string runId)
    {
        var path = Path.Combine(RunsDir, runId + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), Json) : null;
    }

    public List<RunRecord> Recent(int count)
    {
        if (!Directory.Exists(RunsDir)) return [];
        var runs = new List<RunRecord>();
        foreach (var f in Directory.GetFiles(RunsDir, "*.json").OrderDescending().Take(count))
        {
            try
            {
                var run = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f), Json)!;
                MarkIfDead(run);
                runs.Add(run);
            }
            catch (IOException) { } // being written by a running Butler process; picked up on next refresh
            catch (JsonException) { }
        }
        return runs;
    }

    void Save(RunRecord run)
    {
        var path = Path.Combine(RunsDir, run.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(run, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
