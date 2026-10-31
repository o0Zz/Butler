using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Butler;

// Butler.exe                          tray app + dashboard + mail watcher (single instance)
// Butler.exe run <trigger> [var=value ...]  one headless run (what the scheduled tasks and the mail watcher call)
// Butler.exe answer <runId> <text>    resume a run waiting for an answer
// Butler.exe sync                     apply butler.json to the Task Scheduler
static class Program
{
    public const string ShowDashboardEvent = "Butler.ShowDashboard";

    // Local data: settings, runs, state. The triggers file is wherever the settings say (by default with the skills).
    public static string Home { get; } = Environment.GetEnvironmentVariable("BUTLER_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Butler");
    public static string SettingsFile => Path.Combine(Home, "settings.json");
    public static string Exe => Environment.ProcessPath!;
    public static string Version { get; } = ShortVersion(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    // "1.2.0+<commit sha>" (the SDK adds the commit) as "1.2.0 (abc1234)".
    public static string ShortVersion(string informational) => informational.Split('+') is [var version, var sha, ..]
        ? $"{version} ({sha[..Math.Min(7, sha.Length)]})" : informational;

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
                case "answer" when args.Length >= 3: return Report(NewRunner(Settings.Load(SettingsFile)).Answer(args[1], args[2]).GetAwaiter().GetResult());
                default:
                    Notifier.Error("Butler", "Usage: Butler.exe [run <trigger> [var=value ...] | answer <runId> <text>]");
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
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    // The tray app; it lives until Exit in the tray menu, with or without a window.
    sealed class App : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            _ = new TrayApp(this);
            base.OnFrameworkInitializationCompleted();
        }
    }

    static async Task<int> RunTrigger(string name, string[] variables)
    {
        var settings = Settings.Load(SettingsFile);
        var trigger = Trigger.Load(settings.TriggersFile).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (trigger == null) return 0; // removed from butler.json meanwhile: do nothing
        var values = variables.Select(v => v.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]);
        return Report(await NewRunner(settings).Run(trigger.Name, trigger.PromptWith(values)));
    }

    public static Runner NewRunner(Settings settings) => new(Home, Cli.FromName(settings.Cli));

    static int Report(RunRecord run)
    {
        Notifier.Show(run);
        return run.Status == RunStatus.Failed ? 1 : 0;
    }

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
