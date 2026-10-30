using System.Diagnostics;
using System.Text.Json;

namespace Butler.Tests;

// Runs the real Runner against a fake `claude` (a .cmd that prints a canned stream-json output).
public class RunnerTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("butler-").FullName;
    readonly string fake;
    readonly string argsFile;
    readonly string outFile;

    public RunnerTests()
    {
        fake = Path.Combine(home, "claude.cmd");
        argsFile = Path.Combine(home, "args.txt");
        outFile = Path.Combine(home, "out.jsonl");
        File.WriteAllText(fake, "@echo off\r\necho %* > \"%FAKE_ARGS%\"\r\ntype \"%FAKE_OUT%\"\r\nexit /b %FAKE_EXIT%\r\n");
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
    public async Task Waiting_run_is_resumed_in_the_same_session_with_the_answer()
    {
        var runner = new Runner(home, fake);
        ClaudeReplies("Here is the split.\n\nBUTLER-ASK: Write it to the timesheet?");

        var first = await runner.Run("timesheet", "/timesheet month");

        Assert.Equal(RunStatus.Waiting, first.Status);
        Assert.Equal("Write it to the timesheet?", first.Question);
        Assert.Equal(0.12m, first.CostUsd);
        var args = File.ReadAllText(argsFile);
        Assert.Contains("--session-id " + first.SessionId, args);
        Assert.Contains("--output-format stream-json", args);
        Assert.Contains("BUTLER-ASK", args); // the protocol is appended to the system prompt
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
        var runner = new Runner(home, fake);
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
        var run = await new Runner(home, fake).Run("timesheet", "/timesheet");
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
        var runner = new Runner(home, fake);
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
        var hanging = Path.Combine(home, "hang.cmd");
        File.WriteAllText(hanging, "@echo off\r\necho {\"type\":\"system\",\"subtype\":\"init\"}\r\nping -n 60 127.0.0.1 > nul\r\n");
        var runner = new Runner(home, hanging);
        var task = runner.Run("x", "/x");
        var running = await Until(() => runner.Recent(1).FirstOrDefault(r => r.ClaudePid != null));
        Assert.Equal(RunStatus.Running, running.Status);

        runner.Stop(running.Id);

        var run = await task.WaitAsync(TimeSpan.FromSeconds(10)); // returns once claude is killed, not after the 60 s ping
        Assert.Equal(RunStatus.Stopped, run.Status);
        Assert.Equal(RunStatus.Stopped, runner.Load(running.Id)!.Status);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(running.ClaudePid!.Value));
    }

    [Fact]
    public async Task Stop_dismisses_a_waiting_run()
    {
        var runner = new Runner(home, fake);
        ClaudeReplies("BUTLER-ASK: Go?");
        var run = await runner.Run("x", "/x");
        Assert.Equal(RunStatus.Stopped, runner.Stop(run.Id).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Answer(run.Id, "yes"));
    }

    [Fact]
    public void Running_run_whose_process_is_gone_shows_as_stopped()
    {
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        gone.WaitForExit();
        var runner = new Runner(home, fake);
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
        var run = await new Runner(home, Path.Combine(home, "nope.exe")).Run("x", "/x");
        Assert.Equal(RunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task Without_protocol_line_the_first_line_is_the_summary()
    {
        ClaudeReplies("\nAll good, nothing to do.\nDetails...");
        var run = await new Runner(home, fake).Run("x", "/x");
        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Equal("All good, nothing to do.", run.Summary);
    }

    public void Dispose() => Directory.Delete(home, true);
}
