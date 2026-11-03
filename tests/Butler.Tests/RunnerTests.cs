using System.Diagnostics;
using System.Text.Json;

namespace Butler.Tests;

// Runs the real Runner against a fake `claude` (a script that prints a canned stream-json output).
public class RunnerTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("butler-").FullName;
    readonly string fake;
    readonly string argsFile;
    readonly string outFile;

    public RunnerTests()
    {
        fake = Fake.Cli(home);
        argsFile = Path.Combine(home, "args.txt");
        outFile = Path.Combine(home, "out.jsonl");
        Environment.SetEnvironmentVariable("FAKE_ARGS", argsFile);
        Environment.SetEnvironmentVariable("FAKE_OUT", outFile);
        Environment.SetEnvironmentVariable("FAKE_EXIT", "0");
    }

    void ClaudeReplies(string result, bool isError = false) => File.WriteAllLines(outFile,
    [
        """{"type":"system","subtype":"init"}""",
        JsonSerializer.Serialize(new { type = "result", is_error = isError, result, total_cost_usd = 0.12 }),
    ]);

    [Fact]
    public async Task Clear_deletes_finished_runs_and_keeps_the_waiting_ones()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("BUTLER-SUMMARY: done");
        var done = await runner.Run("brief", "/brief");
        ClaudeReplies("BUTLER-ASK: Send it?");
        var waiting = await runner.Run("reply", "/reply");

        runner.Clear();

        Assert.Equal([waiting.Id], runner.Recent(10).Select(r => r.Id));
        Assert.False(File.Exists(Path.Combine(runner.RunsDir, done.Id + ".log")));
        Assert.True(File.Exists(Path.Combine(runner.RunsDir, waiting.Id + ".log")));
    }

    [Fact]
    public async Task Waiting_run_is_resumed_in_the_same_session_with_the_answer()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("Here is the split.\n\nBUTLER-ASK: Write it to the timesheet?");

        var first = await runner.Run("timesheet", "/timesheet month");

        Assert.Equal(RunStatus.Waiting, first.Status);
        Assert.Equal("Write it to the timesheet?", first.Question);
        Assert.Equal(0.12m, first.CostUsd);
        var args = File.ReadAllText(argsFile);
        Assert.Contains("--session-id " + first.SessionId, args);
        Assert.Contains("--output-format stream-json", args);
        Assert.Contains("BUTLER-ASK", args); // the protocol is appended to the system prompt
        Assert.Contains("leave the user something to do", args); // a skill only says "ask Thierry": a draft left to send waits too
        Assert.Contains("\"type\":\"result\"", File.ReadAllText(Path.Combine(runner.RunsDir, first.Id + ".log")));

        ClaudeReplies("Done.\nBUTLER-SUMMARY: 20 days written");
        var second = await runner.Answer(first.Id, "yes");

        Assert.Equal(RunStatus.Done, second.Status);
        Assert.Equal("20 days written", second.Summary);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Contains("--resume " + first.SessionId, File.ReadAllText(argsFile));
        Assert.Equal(RunStatus.Answered, runner.Load(first.Id)!.Status);
        Assert.Equal([second.Id, first.Id], runner.Recent(10).Select(r => r.Id).Order().Reverse());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Answer(first.Id, "yes"));
    }

    [Fact]
    public async Task Nonzero_exit_or_error_result_is_a_failure()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("API error");
        Environment.SetEnvironmentVariable("FAKE_EXIT", "1");
        var run = await runner.Run("x", "/x");
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("API error", run.Summary);

        Environment.SetEnvironmentVariable("FAKE_EXIT", "0");
        ClaudeReplies("Rate limited", isError: true);
        Assert.Equal(RunStatus.Failed, (await runner.Run("x", "/x")).Status);
    }

    [Fact]
    public async Task Reads_claude_output_as_utf8_and_enables_chrome()
    {
        ClaudeReplies("Je n'ai encore rien écrit : « déjà » vérifié.\nBUTLER-SUMMARY: rien à écrire");
        // The ANSI-decoding bug only shows in Butler.exe (no console); here this guards the decoding and the flag.
        var run = await new Runner(home, new ClaudeCli(fake)).Run("timesheet", "/timesheet");
        Assert.Equal("rien à écrire", run.Summary);
        Assert.Contains("« déjà »", run.Result);
        Assert.Contains("--chrome", File.ReadAllText(argsFile));
    }

    [Fact]
    public async Task Activity_lists_what_claude_said_and_did()
    {
        File.WriteAllLines(outFile,
        [
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":""},{"type":"text","text":"Je regarde les commits d'octobre."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git log --since=2026-10-01","description":"List October commits"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"abc"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__claude-in-chrome__navigate","input":{"url":"https://timesheet.example.com"}}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"C:\\x\\SKILL.md"}}]}}""",
            JsonSerializer.Serialize(new { type = "result", is_error = false, result = "BUTLER-SUMMARY: ok" }),
        ]);
        var runner = new Runner(home, new ClaudeCli(fake));
        var run = await runner.Run("timesheet", "/timesheet");
        Assert.Equal(
        [
            "Je regarde les commits d'octobre.",
            "▸ Bash: List October commits",
            "▸ claude-in-chrome navigate: https://timesheet.example.com",
            @"▸ Read: C:\x\SKILL.md",
        ], runner.Activity(run.Id));
    }

    [Fact]
    public async Task Stop_kills_claude_and_the_run_ends_stopped()
    {
        var hanging = Fake.Hanging(home);
        var runner = new Runner(home, new ClaudeCli(hanging));
        var task = runner.Run("x", "/x");
        var running = await Until(() => runner.Recent(1).FirstOrDefault(r => r.ClaudePid != null));
        Assert.Equal(RunStatus.Running, running.Status);

        runner.Stop(running.Id);

        var run = await task.WaitAsync(TimeSpan.FromSeconds(10)); // returns once claude is killed, not after its minute
        Assert.Equal(RunStatus.Stopped, run.Status);
        Assert.Equal(RunStatus.Stopped, runner.Load(running.Id)!.Status);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(running.ClaudePid!.Value));
    }

    [Fact]
    public async Task Stop_dismisses_a_waiting_run()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("BUTLER-ASK: Go?");
        var run = await runner.Run("x", "/x");
        Assert.Equal(RunStatus.Stopped, runner.Stop(run.Id).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Answer(run.Id, "yes"));
    }

    [Fact]
    public void Running_run_whose_process_is_gone_shows_as_stopped()
    {
        using var gone = Process.Start(Fake.Exiting())!;
        gone.WaitForExit();
        var runner = new Runner(home, new ClaudeCli(fake));
        Directory.CreateDirectory(runner.RunsDir);
        File.WriteAllText(Path.Combine(runner.RunsDir, "20260101-000000-000-x.json"), JsonSerializer.Serialize(new
        {
            Id = "20260101-000000-000-x", Trigger = "x", SessionId = "s", Prompt = "/x", Started = DateTime.Now, Status = "Running", Pid = gone.Id,
        }));

        var run = runner.Recent(10).Single();

        Assert.Equal(RunStatus.Stopped, run.Status);
        Assert.Contains("ended before Claude finished", run.Summary);
        Assert.Equal(RunStatus.Stopped, runner.Load(run.Id)!.Status); // saved, so the next refresh agrees
    }

    static async Task<T> Until<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++, await Task.Delay(100))
            if (probe() is { } value) return value;
        throw new TimeoutException();
    }

    [Fact]
    public async Task Missing_claude_is_a_failure_not_a_crash()
    {
        var run = await new Runner(home, new ClaudeCli(Path.Combine(home, "nope.exe"))).Run("x", "/x");
        Assert.Equal(RunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task Without_protocol_line_the_first_line_is_the_summary()
    {
        ClaudeReplies("\nAll good, nothing to do.\nDetails...");
        var run = await new Runner(home, new ClaudeCli(fake)).Run("x", "/x");
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("All good, nothing to do.", run.Summary);
    }

    static string CopilotMessage(string content, string? tool = null, object? arguments = null) => JsonSerializer.Serialize(new
    {
        type = "assistant.message",
        data = new { content, toolRequests = tool == null ? [] : new[] { new { name = tool, arguments } } },
    });

    [Fact]
    public async Task Copilot_run_asks_then_is_resumed_in_the_same_session()
    {
        var runner = new Runner(home, new CopilotCli(fake));
        File.WriteAllLines(outFile,
        [
            """{"type":"session.start","data":{}}""",
            CopilotMessage("", "shell", new { command = "git log", description = "List commits" }),
            """{"type":"tool.execution_complete","data":{"success":true}}""",
            CopilotMessage("Here is the split.\n\nBUTLER-ASK: Write it?"),
            """{"type":"session.shutdown","data":{"totalPremiumRequests":1}}""",
        ]);

        var first = await runner.Run("timesheet", "/timesheet month");

        Assert.Equal(RunStatus.Waiting, first.Status);
        Assert.Equal("Write it?", first.Question);
        Assert.Equal("copilot", first.Cli);
        Assert.Null(first.CostUsd);
        Assert.Equal(["▸ shell: List commits", "Here is the split. Write it?"], runner.Activity(first.Id));

        File.WriteAllLines(outFile, [CopilotMessage("Done.\nBUTLER-SUMMARY: written")]);
        var second = await runner.Answer(first.Id, "yes");

        Assert.Equal(RunStatus.Done, second.Status);
        Assert.Equal("written", second.Summary);
        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public void Copilot_gets_the_protocol_in_the_prompt_and_resumes_by_session_id()
    {
        // Checked here, not through the fake: a .cmd cannot echo an argument with a newline.
        var cli = new CopilotCli();
        Assert.Equal(["-p", "/x\n\nPROTOCOL", "--output-format", "json", "--allow-all-tools", "--no-ask-user", "--session-id", "s1"],
            cli.RunArguments("/x", "PROTOCOL", "s1", resume: false));
        Assert.Equal(cli.RunArguments("yes", "PROTOCOL", "s1", resume: false), cli.RunArguments("yes", "PROTOCOL", "s1", resume: true));
        Assert.Equal(["--resume=s1"], cli.TerminalArguments("s1"));
    }

    [Fact]
    public async Task Waiting_run_is_answered_only_with_the_cli_that_owns_its_session()
    {
        ClaudeReplies("BUTLER-ASK: Go?");
        var run = await new Runner(home, new ClaudeCli(fake)).Run("x", "/x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Runner(home, new CopilotCli(fake)).Answer(run.Id, "yes"));
        Assert.Equal(RunStatus.Waiting, new Runner(home, new ClaudeCli(fake)).Load(run.Id)!.Status);
    }

    [Fact]
    public void Cli_is_chosen_by_name_and_defaults_to_claude()
    {
        Assert.IsType<ClaudeCli>(Cli.FromName(null));
        Assert.IsType<CopilotCli>(Cli.FromName("Copilot"));
        Assert.Throws<ArgumentException>(() => Cli.FromName("gpt"));
    }

    // In the OS's real shell; these lines read the same to cmd and sh.
    [Fact]
    public async Task Command_runs_in_the_shell_without_an_agent()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        var run = await runner.RunCommand("pull", "echo first&& echo done", Platform.Current.Shell.CommandLine("echo first&& echo done"));

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("done", run.Summary);
        Assert.Equal("command", run.Cli);
        Assert.Null(run.CostUsd);
        Assert.Contains("first", run.Result);
        Assert.False(File.Exists(argsFile)); // the agent never started
        Assert.Contains("first", File.ReadAllText(Path.Combine(runner.RunsDir, run.Id + ".log")));
        Assert.Empty(runner.Activity(run.Id)); // once ended, the output is the result
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Answer(run.Id, "yes"));
    }

    [Fact]
    public async Task Command_failing_is_a_failed_run()
    {
        var run = await new Runner(home, new ClaudeCli(fake)).RunCommand("x", "exit 3", Platform.Current.Shell.CommandLine("exit 3"));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("the command failed", run.Summary);
    }

    public void Dispose() => Directory.Delete(home, true);
}
