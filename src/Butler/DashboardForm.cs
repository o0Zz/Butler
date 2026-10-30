using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Butler;

// The dashboard is dashboard.html in a WebView2. C# pushes the whole state as JSON; the page sends commands back.
sealed class DashboardForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    readonly TrayApp tray;
    readonly Runner runner;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    bool ready;
    string? selectedRun; // its activity is pushed with the state

    public DashboardForm(TrayApp tray, Runner runner)
    {
        this.tray = tray;
        this.runner = runner;
        Text = "Butler";
        Size = new Size(1280, 800);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.App;
        Controls.Add(web);
        Load += async (_, _) => await Init();
    }

    async Task Init()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Program.Home, "webview"));
        await web.EnsureCoreWebView2Async(env);
        web.CoreWebView2.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        web.CoreWebView2.WebMessageReceived += (_, e) => OnCommand(e.WebMessageAsJson);
        web.CoreWebView2.NavigationCompleted += (_, _) => { ready = true; RefreshAll(); };
        // Links in Claude's output open in the browser; the dashboard itself never navigates away.
        web.CoreWebView2.NavigationStarting += (_, e) => { if (ready) { e.Cancel = true; OpenLink(e.Uri); } };
        web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenLink(e.Uri); };
        var html = Resource("dashboard.html").Replace("<!--marked-->", $"<script>{Resource("marked.min.js")}</script>");
        web.NavigateToString(html);
    }

    static string Resource(string name)
    {
        using var s = typeof(DashboardForm).Assembly.GetManifestResourceStream("Butler." + name)!;
        return new StreamReader(s).ReadToEnd();
    }

    static void OpenLink(string uri)
    {
        if (uri.StartsWith("https://") || uri.StartsWith("http://")) Open(uri);
    }

    public void RefreshAll() => Push();
    public void RefreshRuns() => Push();

    void Push()
    {
        if (!ready || IsDisposed) return;
        var runs = runner.Recent(100);
        var next = TaskSync.NextRuns();
        var skills = Skills.Load(Program.SkillsDir);
        var state = new
        {
            error = tray.LastError,
            cli = runner.Cli.Name,
            configFile = Program.TriggersFile,
            triggers = tray.Triggers.Select(t => new
            {
                t.Name, t.On, t.Prompt, t.Schedule, t.From,
                skill = Skills.Invoked(t.Prompt),
                skillKnown = Skills.Invoked(t.Prompt) is not { } s || s.Contains(':') || skills.Any(k => k.Name == s || k.Folder == s),
                next = next.TryGetValue(t.Name, out var n) && n > DateTime.MinValue ? n : (DateTime?)null,
                last = runs.FirstOrDefault(r => r.Trigger.Equals(t.Name, StringComparison.OrdinalIgnoreCase)),
            }),
            runs,
            activity = selectedRun == null ? null : new { id = selectedRun, lines = runner.Activity(selectedRun) },
            skills = skills.Select(s => new
            {
                s.Name, s.Description, s.Folder,
                triggers = tray.Triggers.Where(t => Skills.Invoked(t.Prompt) is { } k && (k == s.Name || k == s.Folder)).Select(t => t.Name),
            }),
        };
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state, Json));
    }

    void OnCommand(string json)
    {
        var cmd = JsonSerializer.Deserialize<JsonElement>(json);
        string Arg(string name) => cmd.GetProperty(name).GetString()!;
        switch (Arg("cmd"))
        {
            case "select": selectedRun = Arg("id"); break;
            case "run": Program.Start("run", Arg("name")); break;
            case "answer": Program.Start("answer", Arg("id"), Arg("text")); break;
            case "stop": runner.Stop(Arg("id")); Push(); break;
            case "sync": tray.SyncNow(); break;
            case "editConfig": Open(Program.TriggersFile); break;
            case "openSkills": Process.Start("explorer.exe", Program.SkillsDir); break;
            case "openSkill": Open(Path.Combine(Program.SkillsDir, Arg("folder"), "SKILL.md")); break;
            case "openLog": Open(Path.Combine(runner.RunsDir, Arg("id") + ".log")); break;
            // Edits butler.json and skills; the config watcher then syncs and refreshes this page.
            case "chat": Terminal(Program.SkillsDir, runner.Cli, null); break;
            case "terminal": Terminal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Cli.FromName(Arg("cli")), Arg("session")); break;
        }
        Push();
    }

    // An interactive CLI in Windows Terminal, resuming that session when given.
    static void Terminal(string dir, Cli cli, string? session)
    {
        var psi = new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", dir, cli.Exe } };
        foreach (var a in cli.TerminalArguments(session)) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
