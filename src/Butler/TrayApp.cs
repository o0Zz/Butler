using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Butler;

// Tray icon. Polls the triggers (schedule, mail, ado, file) and shows the dashboard. Runs themselves are separate Butler processes.
sealed class TrayApp
{
    readonly TrayIcon icon = new() { Icon = AppIcon.Tray, ToolTipText = "Butler", IsVisible = true };
    readonly FileSystemWatcher settingsWatcher;
    FileSystemWatcher? triggersWatcher; // follows the triggers file of the settings
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) }; // also refreshes the dashboard (live activity)
    readonly DispatcherTimer reloadDelay = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly ConcurrentDictionary<string, SourceStatus> sources = new(); // by trigger kind: last poll and its error
    Settings settings = new();
    List<Trigger> triggers = [];
    DashboardWindow? dashboard;

    public TrayApp(Application app, Platform platform)
    {
        Platform = platform;
        icon.Menu = [Item("Open dashboard", ShowDashboard), Item("Open folder", () => platform.Shell.OpenFolder(Program.Home)), Item("Exit", Exit)];
        icon.Clicked += (_, _) => ShowDashboard();
        TrayIcon.SetIcons(app, [icon]);

        Directory.CreateDirectory(Program.Home);
        settingsWatcher = Watch(Program.SettingsFile);
        reloadDelay.Tick += (_, _) => { reloadDelay.Stop(); Reload(); };
        statusTimer.Tick += (_, _) => UpdateStatus();

        ShowSignal.Listen(() => Dispatcher.UIThread.Post(ShowDashboard));

        Reload();
        var poller = new Thread(PollLoop) { IsBackground = true, Name = "Butler poll" };
        poller.Start(); // its first poll catches up what came while Butler was not running
        statusTimer.Start();
        UpdateStatus();
    }

    public Platform Platform { get; }
    public IReadOnlyList<Trigger> Triggers => triggers;
    public Settings Settings => settings;
    public IReadOnlyDictionary<string, SourceStatus> Sources => sources;
    public Runner Runner => Program.NewRunner(settings);
    public string? LastError { get; private set; }

    // Re-reads the settings and the triggers file.
    public void Reload()
    {
        try
        {
            settings = Settings.Load(Program.SettingsFile);
            if (triggersWatcher == null || Path.Combine(triggersWatcher.Path, triggersWatcher.Filter) != settings.TriggersFile)
            {
                triggersWatcher?.Dispose();
                triggersWatcher = Watch(settings.TriggersFile);
            }
        }
        catch (Exception ex) { Fail("settings.json", ex); return; }
        try
        {
            triggers = Trigger.Load(settings.TriggersFile);
            LastError = null;
        }
        catch (Exception ex) { Fail(Path.GetFileName(settings.TriggersFile), ex); return; }
        dashboard?.RefreshAll();
    }

    void Fail(string file, Exception ex)
    {
        LastError = $"{file}: {ex.Message}";
        Platform.Notifications.Error($"Butler: {file} not applied", ex.Message);
        dashboard?.RefreshAll();
    }

    FileSystemWatcher Watch(string file)
    {
        var w = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file)) { EnableRaisingEvents = true };
        // Editors write in bursts, and many save by renaming a temp file over it (Created/Renamed, not Changed).
        FileSystemEventHandler later = (_, _) => Dispatcher.UIThread.Post(() => { reloadDelay.Stop(); reloadDelay.Start(); });
        w.Changed += later;
        w.Created += later;
        w.Renamed += (s, e) => later(s, e);
        return w;
    }

    // Polls each kind when it is due, on its own thread: a slow source (az, Outlook) never freezes the tray.
    void PollLoop()
    {
        var due = new Dictionary<string, DateTime>();
        while (true)
        {
            foreach (var kind in TriggerKind.All)
            {
                if (due.GetValueOrDefault(kind.Name) > DateTime.Now) continue;
                due[kind.Name] = DateTime.Now + kind.Interval;
                Poll(kind);
            }
            Thread.Sleep(5_000);
        }
    }

    void Poll(TriggerKind kind)
    {
        var mine = triggers.Where(t => t.On == kind.Name).ToList();
        if (mine.Count == 0)
        {
            sources.TryRemove(kind.Name, out _);
            return;
        }
        string? error = null;
        try
        {
            foreach (var (t, values) in kind.Poll(mine, settings, Path.Combine(Program.Home, "state"), Platform))
                Program.Start(["run", t.Name, .. values.Select(v => $"{v.Key}={v.Value}")]);
        }
        catch (Exception ex) { error = ex.Message; }
        var before = sources.GetValueOrDefault(kind.Name);
        sources[kind.Name] = new SourceStatus(DateTime.Now, error);
        if (error != null && before?.Error == null) // once, not every poll
            Platform.Notifications.Error($"Butler: {kind.Label} triggers failed", error);
    }

    void UpdateStatus()
    {
        var waiting = Runner.Recent(50).Count(r => r.Status == RunStatus.Waiting);
        var problem = LastError ?? sources.Where(s => s.Value.Error != null).Select(s => $"{s.Key} triggers: {s.Value.Error}").FirstOrDefault();
        icon.Icon = problem != null ? AppIcon.TrayError : waiting > 0 ? AppIcon.TrayWaiting : AppIcon.Tray;
        var text = problem ?? (waiting > 0 ? $"Butler: {waiting} waiting for you" : "Butler");
        icon.ToolTipText = text.Length > 63 ? text[..63] : text; // Windows' tray tooltip limit
        dashboard?.RefreshRuns();
    }

    void ShowDashboard()
    {
        if (dashboard != null)
        {
            if (dashboard.WindowState == WindowState.Minimized) dashboard.WindowState = WindowState.Normal;
            dashboard.Activate();
            return;
        }
        dashboard = new DashboardWindow(this);
        dashboard.Closed += (_, _) => dashboard = null;
        dashboard.Show();
    }

    static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }

    void Exit()
    {
        icon.IsVisible = false;
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown();
    }
}

// The last poll of an event source (a trigger kind) and its error, if any.
public sealed record SourceStatus(DateTime Checked, string? Error);
