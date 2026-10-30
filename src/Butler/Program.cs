using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Butler;

// Butler.exe                          tray app + dashboard + mail watcher (single instance)
// Butler.exe run <trigger> [var=value ...]  one headless run (what the scheduled tasks and the mail watcher call)
// Butler.exe answer <runId> <text>    resume a run waiting for an answer
// Butler.exe sync                     apply butler.json to the Task Scheduler
static class Program
{
    public const string ShowDashboardEvent = "Butler.ShowDashboard";

    // The triggers live with the skills they call (a git repo); runs and state are local data.
    public static string TriggersFile { get; } = Environment.GetEnvironmentVariable("BUTLER_CONFIG") is { Length: > 0 } c ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "butler.json");
    public static string SkillsDir => Path.GetDirectoryName(TriggersFile)!;
    public static string Home { get; } = Environment.GetEnvironmentVariable("BUTLER_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Butler");
    public static string Exe => Environment.ProcessPath!;

    [STAThread]
    static int Main(string[] args)
    {
        ToastNotificationManagerCompat.OnActivated += OnToast;
        if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            Thread.Sleep(3000); // OnActivated fires on another thread; give it time to start what it needs
            return 0;
        }

        try
        {
            switch (args.FirstOrDefault())
            {
                case null: return RunTray();
                case "run" when args.Length >= 2: return RunTrigger(args[1], args[2..]).GetAwaiter().GetResult();
                case "answer" when args.Length >= 3: return Report(new Runner(Home).Answer(args[1], args[2]).GetAwaiter().GetResult());
                case "sync": Sync(); return 0;
                default:
                    Notifier.Error("Butler", "Usage: Butler.exe [run <trigger> [var=value ...] | answer <runId> <text> | sync]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Notifier.Error("Butler failed", ex.Message);
            return 1;
        }
    }

    static int RunTray()
    {
        using var mutex = new Mutex(true, "Butler.Tray", out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowDashboardEvent, out var show)) show.Set();
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }

    static async Task<int> RunTrigger(string name, string[] variables)
    {
        var trigger = Trigger.Load(TriggersFile).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (trigger == null) return 0; // removed from butler.json but its task not synced yet: do nothing
        var values = variables.Select(v => v.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]);
        return Report(await new Runner(Home).Run(trigger.Name, trigger.PromptWith(values)));
    }

    static int Report(RunRecord run)
    {
        Notifier.Show(run);
        return run.Status == RunStatus.Failed ? 1 : 0;
    }

    public static TaskSync.Result Sync() => TaskSync.Sync(Trigger.Load(TriggersFile), Exe);

    // Starts another Butler process (runs never live inside the tray process, so closing it kills nothing).
    public static void Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    static void OnToast(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("run", out var runId)) return;
        if (args.TryGetValue("answer", out var answer)) Start("answer", runId, answer);
        else Start(); // opens the dashboard, or brings it up if the tray app already runs
    }
}
