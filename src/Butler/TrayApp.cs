using System.Diagnostics;

namespace Butler;

// Tray icon. Keeps the Task Scheduler in sync with butler.json, polls Outlook for mail triggers,
// and shows the dashboard. Runs themselves are separate Butler processes.
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon icon = new() { Icon = AppIcon.Tray, Text = "Butler", Visible = true };
    readonly SynchronizationContext ui;
    readonly FileSystemWatcher watcher;
    readonly System.Windows.Forms.Timer mailTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2_000 }; // also refreshes the dashboard (live activity)
    readonly System.Windows.Forms.Timer reloadDelay = new() { Interval = 1_000 };
    readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, Program.ShowDashboardEvent);
    readonly Runner runner = new(Program.Home);
    readonly string checkpointFile = Path.Combine(Program.Home, "state", "mail.json");
    List<Trigger> triggers = [];
    DashboardForm? dashboard;
    string? mailError;

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

        watcher = new FileSystemWatcher(Program.SkillsDir, Path.GetFileName(Program.TriggersFile)) { EnableRaisingEvents = true };
        // Editors write in bursts, and many save by renaming a temp file over it (Created/Renamed, not Changed).
        FileSystemEventHandler later = (_, _) => ui.Post(_ => { reloadDelay.Stop(); reloadDelay.Start(); }, null);
        watcher.Changed += later;
        watcher.Created += later;
        watcher.Renamed += (s, e) => later(s, e);
        reloadDelay.Tick += (_, _) => { reloadDelay.Stop(); Reload(); };
        mailTimer.Tick += (_, _) => PollMail();
        statusTimer.Tick += (_, _) => UpdateStatus();

        new Thread(() => { while (showEvent.WaitOne()) ui.Post(_ => ShowDashboard(), null); }) { IsBackground = true }.Start();

        Reload();
        PollMail(); // catch up mails received while Butler was not running
        mailTimer.Start();
        statusTimer.Start();
        UpdateStatus();
    }

    public IReadOnlyList<Trigger> Triggers => triggers;
    public string? LastError { get; private set; }

    void Reload()
    {
        try
        {
            triggers = Trigger.Load(Program.TriggersFile);
            var r = TaskSync.Sync(triggers, Program.Exe);
            LastError = null;
            var changes = r.Created.Select(n => "+" + n).Concat(r.Updated.Select(n => "~" + n)).Concat(r.Deleted.Select(n => "-" + n)).ToList();
            if (changes.Count > 0) icon.ShowBalloonTip(3000, "Butler synced", string.Join(" ", changes), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            LastError = "butler.json: " + ex.Message;
            icon.ShowBalloonTip(5000, "Butler: butler.json not applied", ex.Message, ToolTipIcon.Error);
        }
        dashboard?.RefreshAll();
    }

    void PollMail()
    {
        if (!triggers.Any(t => t.On == "mail") || !OutlookInbox.IsRunning) return;
        try
        {
            var checkpoint = MailCheckpoint.Load(checkpointFile);
            foreach (var mail in checkpoint.TakeNew(OutlookInbox.ReadSince(checkpoint.Last)))
                foreach (var t in MailCheckpoint.Matching(triggers, mail))
                    Program.Start(["run", t.Name, .. MailCheckpoint.VariablesFor(mail)]);
            checkpoint.Save(checkpointFile);
            mailError = null;
        }
        catch (Exception ex)
        {
            if (mailError == null) icon.ShowBalloonTip(5000, "Butler: cannot read Outlook", ex.Message, ToolTipIcon.Warning);
            mailError = ex.Message;
        }
    }

    void UpdateStatus()
    {
        var waiting = runner.Recent(50).Count(r => r.Status == RunStatus.Waiting);
        var problem = LastError ?? (mailError == null ? null : "Outlook: " + mailError);
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
        dashboard = new DashboardForm(this, runner);
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
