using System.Text.Json;

namespace Butler;

// What one output line of a CLI says: a message, a tool call, or the end of the run.
public abstract record CliEvent;
public sealed record CliText(string Text) : CliEvent;
public sealed record CliTool(string Name, JsonElement Input) : CliEvent;
public sealed record CliResult(string? Result, bool IsError, decimal? CostUsd) : CliEvent;

// The agent CLI that runs the triggers: claude or copilot, chosen in the settings.
// Both print one JSON event per line and can start a session with a given id, then resume it.
public abstract class Cli(string name, string exe)
{
    public string Name { get; } = name;
    public string Exe { get; } = exe;

    public static readonly string[] Names = ["claude", "copilot"];

    public static Cli FromName(string name) => name.ToLowerInvariant() switch
    {
        "claude" => new ClaudeCli(),
        "copilot" => new CopilotCli(),
        _ => throw new ArgumentException($"Unknown CLI \"{name}\": use claude or copilot"),
    };

    // Where the executable is on the PATH, or null.
    public string? Find(IShell shell, string? path = null)
    {
        var file = shell.ExecutableName(Exe);
        foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.Combine(dir.Trim(), file) is var f && File.Exists(f)) return f;
        return null;
    }

    // A headless run in a new session with that id, or resuming it. `protocol` is Butler's instructions to the agent.
    // model: --model, null for the CLI's default.
    public abstract IEnumerable<string> RunArguments(string prompt, string protocol, string sessionId, bool resume, string? model);

    protected static string[] ModelArguments(string? model) => model == null ? [] : ["--model", model];

    // A quick headless run (a hotkey trigger): the prompt alone, without tools nor MCP servers, `model` when given.
    // `protocol` says the answer is used as is. prompt null: it stays running and reads the prompts on its input
    // (CanStayRunning, QuickAgent). mine: with the user's own config (CLAUDE.md, settings, hooks), else only the prompt.
    public abstract IEnumerable<string> QuickArguments(string? prompt, string protocol, string? model, bool mine);

    // Whether QuickArguments(null, ...) works: one process answering prompt after prompt.
    public virtual bool CanStayRunning => false;

    // An interactive session in a terminal, resuming that session when given.
    public abstract IEnumerable<string> TerminalArguments(string? sessionId);

    // The events of one output line; nothing for lines that do not matter. Throws on shapes it does not know.
    public abstract IEnumerable<CliEvent> Read(JsonElement line);
}

// Claude Code: stream-json, the protocol appended to the system prompt, a final "result" event with the cost.
public sealed class ClaudeCli(string exe = "claude") : Cli("claude", exe)
{
    public override IEnumerable<string> RunArguments(string prompt, string protocol, string sessionId, bool resume, string? model) =>
        ["-p", prompt, "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--chrome",
         "--append-system-prompt", protocol, resume ? "--resume" : "--session-id", sessionId, .. ModelArguments(model)];

    public override bool CanStayRunning => true;

    // Without the user's config: its own system prompt and no user settings (no hooks: about a second less), started out
    // of the user folder (QuickAgent.Folder), where the "project" settings would be the user's. Slash commands stay: /clear.
    public override IEnumerable<string> QuickArguments(string? prompt, string protocol, string? model, bool mine) =>
        ["-p", .. prompt == null ? new[] { "--input-format", "stream-json" } : [prompt], "--output-format", "stream-json", "--verbose",
         "--strict-mcp-config", "--tools", "", "--no-session-persistence", "--no-chrome",
         .. mine ? new[] { "--append-system-prompt", protocol } : ["--system-prompt", protocol, "--setting-sources", "project"],
         .. ModelArguments(model)];

    public override IEnumerable<string> TerminalArguments(string? sessionId) => sessionId == null ? [] : ["--resume", sessionId];

    public override IEnumerable<CliEvent> Read(JsonElement line)
    {
        switch (line.GetProperty("type").GetString())
        {
            case "assistant":
                foreach (var c in line.GetProperty("message").GetProperty("content").EnumerateArray())
                {
                    var type = c.GetProperty("type").GetString();
                    if (type == "text" && c.GetProperty("text").GetString() is { Length: > 0 } text) yield return new CliText(text);
                    else if (type == "tool_use") yield return new CliTool(c.GetProperty("name").GetString()!, c.GetProperty("input").Clone());
                }
                break;
            case "result":
                yield return new CliResult(
                    line.TryGetProperty("result", out var r) ? r.GetString() : null,
                    line.TryGetProperty("is_error", out var e) && e.GetBoolean(),
                    line.TryGetProperty("total_cost_usd", out var cost) ? cost.GetDecimal() : null);
                break;
        }
    }
}

// GitHub Copilot CLI: its JSON output is its session events. No system prompt option (the protocol goes in the prompt),
// no final result event (the result is the last message, failure is the exit code), no cost in dollars.
// Non-interactive runs need --allow-all-tools: unlike claude's auto mode, no classifier reviews what it does.
public sealed class CopilotCli(string exe = "copilot") : Cli("copilot", exe)
{
    public override IEnumerable<string> RunArguments(string prompt, string protocol, string sessionId, bool resume, string? model) =>
        ["-p", prompt + "\n\n" + protocol, "--output-format", "json", "--allow-all-tools", "--no-ask-user", "--session-id", sessionId, .. ModelArguments(model)];

    // Without --allow-all-tools, a tool it would want is refused.
    public override IEnumerable<string> QuickArguments(string? prompt, string protocol, string? model, bool mine) =>
        ["-p", prompt + "\n\n" + protocol, "--output-format", "json", "--no-ask-user", .. ModelArguments(model)];

    public override IEnumerable<string> TerminalArguments(string? sessionId) => sessionId == null ? [] : ["--resume=" + sessionId];

    public override IEnumerable<CliEvent> Read(JsonElement line)
    {
        if (line.GetProperty("type").GetString() != "assistant.message") yield break;
        var data = line.GetProperty("data");
        if (data.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text) yield return new CliText(text);
        if (data.TryGetProperty("toolRequests", out var tools))
            foreach (var t in tools.EnumerateArray())
                yield return new CliTool(t.GetProperty("name").GetString()!, t.TryGetProperty("arguments", out var a) ? a.Clone() : default);
    }
}
