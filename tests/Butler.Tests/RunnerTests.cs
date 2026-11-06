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

        var first = await runner.Run("timesheet", "/timesheet month", "opus");

        Assert.Equal(RunStatus.Waiting, first.Status);
        Assert.Equal("opus", first.Model);
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
        Assert.Contains("--model opus", File.ReadAllText(argsFile)); // the answer continues with the run's model
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
            cli.RunArguments("/x", "PROTOCOL", "s1", resume: false, model: null));
        Assert.Equal(cli.RunArguments("yes", "PROTOCOL", "s1", resume: false, null), cli.RunArguments("yes", "PROTOCOL", "s1", resume: true, null));
        Assert.Equal(["--model", "gpt-5"], cli.RunArguments("/x", "PROTOCOL", "s1", resume: false, model: "gpt-5").TakeLast(2));
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

    [Fact]
    public async Task A_quick_run_has_no_tools_nor_protocol_and_its_answer_is_the_result()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("I went to the meeting.\n");

        var run = await runner.Quick("fix", "Fix: i has went", "sonnet", mine: false);

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("I went to the meeting.", run.Result); // as is, for the selection
        Assert.Equal("I went to the meeting.", run.Summary);
        var args = File.ReadAllText(argsFile);
        Assert.Contains("--strict-mcp-config --tools", args); // no MCP servers, no tools: seconds
        Assert.Contains("--model sonnet", args);
        Assert.DoesNotContain("BUTLER-", args);
    }

    [Fact]
    public void A_quick_run_skips_the_users_config_unless_asked()
    {
        var cli = new ClaudeCli();
        var light = cli.QuickArguments("Fix: x", "PROTOCOL", null, mine: false).ToList();
        Assert.Equal("", light[light.IndexOf("--tools") + 1]); // the empty list (the scripts echo it differently per OS)
        Assert.Equal(["--system-prompt", "PROTOCOL", "--setting-sources", "project"], light.Skip(light.IndexOf("--system-prompt")).Take(4));
        Assert.Contains("--no-session-persistence", light);
        var mine = cli.QuickArguments("Fix: x", "PROTOCOL", null, mine: true).ToList();
        Assert.DoesNotContain("--setting-sources", mine);
        Assert.Equal(["--append-system-prompt", "PROTOCOL"], mine.Skip(mine.IndexOf("--append-system-prompt")).Take(2));
        var staying = cli.QuickArguments(null, "PROTOCOL", "haiku", mine: false).ToList();
        Assert.Equal(["-p", "--input-format", "stream-json"], staying.Take(3));
        Assert.Equal(["--model", "haiku"], staying.TakeLast(2));
        Assert.Equal(QuickAgent.Folder(false, home), home); // where the "project" settings are none
    }

    [Fact]
    public async Task A_quick_run_without_an_answer_fails()
    {
        var runner = new Runner(home, new ClaudeCli(fake));
        ClaudeReplies("  ");
        Assert.Equal(RunStatus.Failed, (await runner.Quick("fix", "Fix: x", null, mine: false)).Status);
        Assert.DoesNotContain("--model", File.ReadAllText(argsFile)); // the CLI's default model

        ClaudeReplies("Credit balance is too low", isError: true);
        var failed = await runner.Quick("fix", "Fix: x", null, mine: false);
        Assert.Equal((RunStatus.Failed, "Credit balance is too low"), (failed.Status, failed.Summary));
    }

    [Fact]
    public async Task The_cli_kept_running_answers_prompt_after_prompt_and_clears_between()
    {
        var input = Path.Combine(home, "in.txt");
        Environment.SetEnvironmentVariable("FAKE_IN", input);
        var runner = new Runner(home, new ClaudeCli(Fake.Serving(home)));
        using var agent = new QuickAgent(runner.Cli, "sonnet", mine: false, home);

        var first = await runner.Quick("fix", "Fix: one", agent);
        var second = await runner.Quick("fix", "Fix: two", agent);

        Assert.Equal((RunStatus.Done, "answer 1", "sonnet"), (first.Status, first.Result, first.Model));
        Assert.Equal("answer 3", second.Result); // answer 2 was the /clear's
        Assert.Equal(0.01m, second.CostUsd);
        var sent = File.ReadAllLines(input).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(["Fix: one", "/clear", "Fix: two", "/clear"], sent);
        Assert.Single(File.ReadAllLines(argsFile)); // one process for both
        Assert.Contains("--input-format stream-json", File.ReadAllText(argsFile));
        Assert.Contains("\"result\":\"answer 1\"", File.ReadAllText(Path.Combine(runner.RunsDir, first.Id + ".log")));

        agent.Dispose(); // as when Butler exits; the next use starts another
        Assert.Equal("answer 1", (await runner.Quick("fix", "Fix: three", agent)).Result);
        Assert.Equal(2, File.ReadAllLines(argsFile).Length);
    }

    [Fact]
    public async Task A_cli_kept_running_that_hangs_is_killed_and_the_run_fails()
    {
        using var agent = new QuickAgent(new ClaudeCli(Fake.Hanging(home)), null, mine: false, home);
        await Assert.ThrowsAsync<TimeoutException>(() => agent.Ask("Fix: x", _ => { }, TimeSpan.FromSeconds(2)));
        Assert.Null(agent.Pid); // killed: the next prompt starts a new one
    }

    // The real Claude Code, kept running: a second prompt in about a second, and /clear forgets the first.
    // Costs a few cents: skipped unless BUTLER_TEST_CLAUDE is set.
    [SkippableFact]
    public async Task Real_claude_kept_running_is_quick_and_forgets_between_prompts()
    {
        Skip.If(Environment.GetEnvironmentVariable("BUTLER_TEST_CLAUDE") == null, "BUTLER_TEST_CLAUDE is not set");
        using var agent = new QuickAgent(new ClaudeCli(), "sonnet", mine: false, home);
        var first = await agent.Ask("Remember the secret word PAMPLEMOUSSE and answer only OK.", _ => { });
        var watch = Stopwatch.StartNew();
        var second = await agent.Ask("What is the secret word? If you do not know it, answer only UNKNOWN.", _ => { });
        Assert.Equal("OK", first.Result?.Trim());
        Assert.Equal("UNKNOWN", second.Result?.Trim());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    public void Dispose() => Directory.Delete(home, true);
}
