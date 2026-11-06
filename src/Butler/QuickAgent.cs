using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Butler;

// The CLI kept running for hotkey runs (Cli.CanStayRunning): a prompt then takes about a second instead of four.
// Started at the first use (nobody without hotkeys has one), then each prompt written to its input, /clear after each
// so that the next one never sees the text before. One per CLI, model and config (TrayApp), closed with the tray;
// if the tray dies, its input closes and the CLI ends. A prompt that hangs kills it: the next one starts a new one.
public sealed class QuickAgent(Cli cli, string? model, bool mine, string home) : IDisposable
{
    readonly SemaphoreSlim one = new(1, 1); // one prompt at a time: a second hotkey waits
    readonly StringBuilder stderr = new();
    Process? process;

    public string? Model => model;
    public int? Pid => process?.Id;

    // Where it starts: the user folder with the user's config, else Butler's (no "project" settings there).
    public static string Folder(bool mine, string home) => mine ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;

    // The answer to the prompt, its output lines also to `log`. Throws a TimeoutException (it is then killed)
    // or an IOException (it ended).
    public async Task<CliResult> Ask(string prompt, Action<string> log, TimeSpan? timeout = null)
    {
        await one.WaitAsync();
        try
        {
            if (process is not { HasExited: false }) Start();
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
            try
            {
                var answer = await Send(prompt, log, cancel.Token);
                await Send("/clear", _ => { }, cancel.Token);
                return answer;
            }
            catch (OperationCanceledException)
            {
                Stop();
                throw new TimeoutException($"{cli.Name} gave no answer within {(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds:0} s");
            }
            catch (IOException)
            {
                Stop();
                throw;
            }
        }
        finally { one.Release(); }
    }

    async Task<CliResult> Send(string text, Action<string> log, CancellationToken cancel)
    {
        var p = process!;
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } }));
        await p.StandardInput.FlushAsync(cancel);
        while (await p.StandardOutput.ReadLineAsync(cancel) is { } line)
        {
            log(line);
            if (Runner.Events(cli, line).OfType<CliResult>().FirstOrDefault() is { } result) return result;
        }
        string error;
        lock (stderr) error = stderr.ToString().Trim();
        throw new IOException($"{cli.Name} ended: {(error.Length > 0 ? error : "no answer")}");
    }

    void Start()
    {
        var psi = new ProcessStartInfo(cli.Exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), // a BOM would spoil the first JSON line
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Folder(mine, home),
        };
        foreach (var a in cli.QuickArguments(null, Runner.QuickProtocol, model, mine)) psi.ArgumentList.Add(a);
        lock (stderr) stderr.Clear();
        process = Process.Start(psi)!;
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
    }

    void Stop()
    {
        var p = process;
        process = null;
        if (p == null) return;
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // already gone
        p.Dispose();
    }

    // Not waiting for a prompt in progress: it fails, and its run says so.
    public void Dispose() => Stop();
}
