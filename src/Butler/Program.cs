using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;

namespace Butler;

// Butler                              tray app + dashboard + trigger polling (single instance)
// Butler run <trigger> [var=value ...]  one headless run (what the tray starts for a trigger)
// Butler answer <runId> <text>        resume a run waiting for an answer
// Butler updated                      the tray, once the previous version's tray has exited (after an update)
static class Program
{
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
        var platform = Platform.Current; // the OS's services, handed from here to what needs them
        if (platform.Notifications.StartedByClick()) return 0;

        try
        {
            switch (args.FirstOrDefault())
            {
                case null: return RunTray(platform);
                case "updated": return RunTray(platform, updated: true);
                case "run" when args.Length >= 2: return RunTrigger(platform, args[1], args[2..]).GetAwaiter().GetResult();
                case "answer" when args.Length >= 3:
                    return Report(platform.Notifications, NewRunner(LoadSettings()).Answer(args[1], args[2]).GetAwaiter().GetResult());
                default:
                    platform.Notifications.Error("Butler", "Usage: Butler [run <trigger> [var=value ...] | answer <runId> <text>]");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            platform.Notifications.Error(T("Butler failed"), ex.Message);
            return 1;
        }
    }

    // updated: started by the previous version's tray (TrayApp.Update), which is exiting.
    static int RunTray(Platform platform, bool updated = false)
    {
        using var mutex = new Mutex(true, "Butler.Tray", out var first);
        if (!first && updated)
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { first = true; } // its process ended without releasing it: ours now
        }
        if (!first)
        {
            ShowSignal.Send();
            return 0;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)); // the previous version's file is freed a moment after its process
            try { File.Delete(Exe + ".old"); } // left by an update; a run of the old version still going keeps it until the next start
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
        AppBuilder.Configure(() => new App(platform)).UsePlatformDetect().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    // The tray app; it lives until Exit in the tray menu, with or without a window.
    sealed class App(Platform platform) : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            _ = new TrayApp(this, platform);
            base.OnFrameworkInitializationCompleted();
        }
    }

    static async Task<int> RunTrigger(Platform platform, string name, string[] variables)
    {
        var settings = LoadSettings();
        var trigger = Trigger.Load(settings.TriggersFile).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (trigger == null) return 0; // removed from butler.json meanwhile: do nothing
        var values = variables.Select(v => v.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]);
        var runner = NewRunner(settings);
        if (trigger.Kind is HotkeyTrigger)
        {
            // When it works, the tray puts the answer in place of the selection: no notification.
            var quick = await runner.Quick(trigger.Name, trigger.PromptWith(values), settings.ModelFor(trigger.Model), HotkeyTrigger.MyConfig(trigger));
            return quick.Status == RunStatus.Done ? 0 : Report(platform.Notifications, quick);
        }
        if (trigger.Command is not { } command)
            return Report(platform.Notifications, await runner.Run(trigger.Name, trigger.PromptWith(values), settings.ModelFor(trigger.Model)));
        var shell = platform.Shell.CommandLine(command);
        foreach (var (k, v) in Trigger.Environment(values)) shell.Environment[k] = v;
        return Report(platform.Notifications, await runner.RunCommand(trigger.Name, command, shell));
    }

    // For a run: its notifications and summaries in the language of the settings.
    static Settings LoadSettings()
    {
        var settings = Settings.Load(SettingsFile);
        Lang.Use(settings.Language);
        return settings;
    }

    public static Runner NewRunner(Settings settings) => new(Home, Cli.FromName(settings.Cli));

    static int Report(INotifications notifications, RunRecord run)
    {
        notifications.Show(run);
        return run.Status == RunStatus.Failed ? 1 : 0;
    }

    // Starts another Butler process (runs never live inside the tray process, so closing it kills nothing).
    public static Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }
}
