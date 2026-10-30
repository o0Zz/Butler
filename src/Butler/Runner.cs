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
    public DateTime Started { get; init; }
    public DateTime? Ended { get; set; }
    public RunStatus Status { get; set; }
    public string? Summary { get; set; }
    public string? Question { get; set; }
    public string? Result { get; set; }
    public decimal? CostUsd { get; set; }
    public int? Pid { get; init; }      // the process that runs it and records the outcome: if it is gone, the run is dead
    public int? ClaudePid { get; set; } // what Stop kills
}

// Runs `claude -p` hidden, records the run in <home>/runs, and reports the outcome.
public sealed class Runner(string home, string claudeExe = "claude")
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public string RunsDir { get; } = Path.Combine(home, "runs");

    public static string Protocol(string trigger) =>
        $"You are running unattended, launched by Butler (trigger: {trigger}). Nobody can answer you during this run. " +
        "If you need a decision or a confirmation from the user before going further, stop there and end your final message " +
        "with one line: `BUTLER-ASK: <short question>`. The user will answer later in this same session. " +
        "Otherwise end your final message with one line: `BUTLER-SUMMARY: <one-line outcome>`.";

    public async Task<RunRecord> Run(string trigger, string prompt, string? resumeSession = null)
    {
        Directory.CreateDirectory(RunsDir);
        var run = new RunRecord
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{trigger}", // sortable: Recent() orders by file name
            Trigger = trigger,
            SessionId = resumeSession ?? Guid.NewGuid().ToString(),
            Prompt = prompt,
            Started = DateTime.Now,
            Status = RunStatus.Running,
            Pid = Environment.ProcessId,
        };
        Save(run);

        var psi = new ProcessStartInfo(claudeExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, // default is the ANSI code page: "é" came out as "Ã©"
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            // Not the Butler repo: its CLAUDE.md is about developing Butler, not about the triggered task.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in new[] { "-p", prompt, "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--chrome",
                     "--append-system-prompt", Protocol(trigger) })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(resumeSession == null ? "--session-id" : "--resume");
        psi.ArgumentList.Add(run.SessionId);

        string? result = null;
        bool isError = false;
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
                if (!line.Contains("\"type\":\"result\"")) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                result = root.TryGetProperty("result", out var r) ? r.GetString() : null;
                isError = root.TryGetProperty("is_error", out var e) && e.GetBoolean();
                if (root.TryGetProperty("total_cost_usd", out var c)) run.CostUsd = c.GetDecimal();
            }
            await p.WaitForExitAsync();
            var err = await stderr;
            if (err.Length > 0) await log.WriteLineAsync("[stderr] " + err);
            if (p.ExitCode != 0) isError = true;
            if (result == null && err.Length > 0) result = err.Trim();
        }
        catch (Exception ex)
        {
            isError = true;
            result = ex.Message;
        }

        if (Load(run.Id) is { Status: RunStatus.Stopped } stopped) return stopped; // Stop killed claude: keep its outcome
        run.Ended = DateTime.Now;
        run.Result = result;
        Interpret(run, isError);
        Save(run);
        return run;
    }

    // Marks the waiting run as answered and resumes its session with the answer.
    public Task<RunRecord> Answer(string runId, string answer)
    {
        var waiting = Load(runId) ?? throw new ArgumentException($"Unknown run {runId}");
        if (waiting.Status != RunStatus.Waiting) throw new InvalidOperationException($"Run {runId} is not waiting for an answer");
        waiting.Status = RunStatus.Answered;
        waiting.Summary = $"Answered: {answer}";
        Save(waiting);
        return Run(waiting.Trigger, answer, waiting.SessionId);
    }

    // Kills claude and what it started (the log is kept). A waiting run is only dismissed.
    public RunRecord Stop(string runId)
    {
        var run = Load(runId) ?? throw new ArgumentException($"Unknown run {runId}");
        if (run.Status is not (RunStatus.Running or RunStatus.Waiting)) return run;
        var claude = run.Status == RunStatus.Running ? Live(run.ClaudePid, p => p.StartTime >= run.Started.AddSeconds(-2)) : null;
        run.Status = RunStatus.Stopped;
        run.Ended = DateTime.Now;
        run.Summary = "Stopped from the dashboard";
        Save(run); // before the kill, so the run process sees it and keeps it
        if (claude != null)
        {
            claude.Kill(entireProcessTree: true);
            claude.WaitForExit(5000);
        }
        return run;
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
        run.Summary = "Stopped: the Butler process running it ended before Claude finished (killed, sign-out or reboot)";
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
            run.Summary = lines.FirstOrDefault(l => l.Length > 0) ?? "claude failed";
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

    // What Claude said and which tools it used, one short line each, read from the raw stream (also while it runs).
    public List<string> Activity(string runId)
    {
        var lines = new List<string>();
        var path = Path.Combine(RunsDir, runId + ".log");
        if (!File.Exists(path)) return lines;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("{\"type\":\"assistant\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                foreach (var c in doc.RootElement.GetProperty("message").GetProperty("content").EnumerateArray())
                {
                    var type = c.GetProperty("type").GetString();
                    if (type == "text" && c.GetProperty("text").GetString() is { Length: > 0 } text)
                        lines.Add(Short(text.Replace("BUTLER-ASK:", "").Replace("BUTLER-SUMMARY:", "").Trim(), 300));
                    else if (type == "tool_use")
                        lines.Add($"▸ {ToolName(c.GetProperty("name").GetString()!)}{ToolDetail(c.GetProperty("input"))}");
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { } // partial last line
        }
        return lines;
    }

    // "mcp__claude-in-chrome__navigate" -> "claude-in-chrome navigate"
    static string ToolName(string name) => name.StartsWith("mcp__") ? name[5..].Replace("__", " ") : name;

    static string ToolDetail(JsonElement input)
    {
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
