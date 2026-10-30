using System.Collections.Concurrent;
using System.Diagnostics;

namespace Butler;

// Tray icon. Keeps the Task Scheduler in sync with the triggers file, polls the event triggers (mail, ado, file),
// and shows the dashboard. Runs themselves are separate Butler processes.
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon icon = new() { Icon = AppIcon.Tray, Text = "Butler", Visible = true };
    readonly SynchronizationContext ui;
    readonly FileSystemWatcher settingsWatcher;
    FileSystemWatcher? triggersWatcher; // follows the triggers file of the settings
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2_000 }; // also refreshes the dashboard (live activity)
    readonly System.Windows.Forms.Timer reloadDelay = new() { Interval = 1_000 };
    readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, Program.ShowDashboardEvent);
    readonly ConcurrentDictionary<string, SourceStatus> sources = new(); // by trigger kind: last poll and its error
    Settings settings = new();
    List<Trigger> triggers = [];
    DashboardForm? dashboard;

    public TrayApp()
    {
        ui = new WindowsFormsSynchronizationContext(); // Current is still null: no control exists before Application.Run
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Sync now", null, (_, _) => Reload());
        menu.Items.Add("Open folder", null, (_, _) => Process.Start("explorer.exe", Program.Home));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowDashboard();

        Directory.CreateDirectory(Program.Home);
        settingsWatcher = Watch(Program.SettingsFile);
        reloadDelay.Tick += (_, _) => { reloadDelay.Stop(); Reload(); };
        statusTimer.Tick += (_, _) => UpdateStatus();

        new Thread(() => { while (showEvent.WaitOne()) ui.Post(_ => ShowDashboard(), null); }) { IsBackground = true }.Start();

        Reload();
        var poller = new Thread(PollLoop) { IsBackground = true, Name = "Butler poll" };
        poller.SetApartmentState(ApartmentState.STA); // Outlook COM
        poller.Start(); // its first poll catches up what came while Butler was not running
        statusTimer.Start();
        UpdateStatus();
    }

    public IReadOnlyList<Trigger> Triggers => triggers;
    public Settings Settings => settings;
    public IReadOnlyDictionary<string, SourceStatus> Sources => sources;
    public Runner Runner => Program.NewRunner(settings);
    public string? LastError { get; private set; }

    // Re-reads the settings and the triggers file, then syncs the Task Scheduler.
    void Reload()
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
            var r = TaskSync.Sync(triggers, Program.Exe);
            LastError = null;
            var changes = r.Created.Select(n => "+" + n).Concat(r.Updated.Select(n => "~" + n)).Concat(r.Deleted.Select(n => "-" + n)).ToList();
            if (changes.Count > 0) icon.ShowBalloonTip(3000, "Butler synced", string.Join(" ", changes), ToolTipIcon.Info);
        }
        catch (Exception ex) { Fail(Path.GetFileName(settings.TriggersFile), ex); return; }
        dashboard?.RefreshAll();
    }

    void Fail(string file, Exception ex)
    {
        LastError = $"{file}: {ex.Message}";
        icon.ShowBalloonTip(5000, $"Butler: {file} not applied", ex.Message, ToolTipIcon.Error);
        dashboard?.RefreshAll();
    }

    FileSystemWatcher Watch(string file)
    {
        var w = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file)) { EnableRaisingEvents = true };
        // Editors write in bursts, and many save by renaming a temp file over it (Created/Renamed, not Changed).
        FileSystemEventHandler later = (_, _) => ui.Post(_ => { reloadDelay.Stop(); reloadDelay.Start(); }, null);
        w.Changed += later;
        w.Created += later;
        w.Renamed += (s, e) => later(s, e);
        return w;
    }

    // Polls each event kind when it is due, on its own thread: a slow source (az, Outlook) never freezes the tray.
    void PollLoop()
    {
        var due = new Dictionary<string, DateTime>();
        while (true)
        {
            foreach (var kind in TriggerKind.All)
            {
                if (kind.Interval is not { } every || due.GetValueOrDefault(kind.Name) > DateTime.Now) continue;
                due[kind.Name] = DateTime.Now + every;
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
            foreach (var (t, values) in kind.Poll(mine, settings, Path.Combine(Program.Home, "state")))
                Program.Start(["run", t.Name, .. values.Select(v => $"{v.Key}={v.Value}")]);
        }
        catch (Exception ex) { error = ex.Message; }
        var before = sources.GetValueOrDefault(kind.Name);
        sources[kind.Name] = new SourceStatus(DateTime.Now, error);
        if (error != null && before?.Error == null) // once, not every poll
            ui.Post(_ => icon.ShowBalloonTip(5000, $"Butler: {kind.Label} triggers failed", error, ToolTipIcon.Warning), null);
    }

    void UpdateStatus()
    {
        var waiting = Runner.Recent(50).Count(r => r.Status == RunStatus.Waiting);
        var problem = LastError ?? sources.Where(s => s.Value.Error != null).Select(s => $"{s.Key} triggers: {s.Value.Error}").FirstOrDefault();
        icon.Icon = problem != null ? AppIcon.TrayError : waiting > 0 ? AppIcon.TrayWaiting : AppIcon.Tray;
        var text = problem ?? (waiting > 0 ? $"Butler: {waiting} waiting for you" : "Butler");
        icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        dashboard?.RefreshRuns();
    }

    void ShowDashboard()
    {
        if (dashboard is { IsDisposed: false })
        {
            dashboard.WindowState = FormWindowState.Normal;
            dashboard.Activate();
            return;
        }
        dashboard = new DashboardForm(this);
        dashboard.Show();
    }

    public void SyncNow() => Reload();

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        icon.Dispose();
        base.ExitThreadCore();
    }
}

// The last poll of an event source (a trigger kind) and its error, if any.
public sealed record SourceStatus(DateTime Checked, string? Error);
