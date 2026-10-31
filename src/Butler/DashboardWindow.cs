using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Butler;

// The dashboard is dashboard.html in the system's web view (WebView2 on Windows). C# pushes the whole state as JSON
// (receive), the page sends commands back (invokeCSharpAction).
sealed class DashboardWindow : Window
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    readonly TrayApp tray;
    readonly NativeWebView web = new();
    bool ready, closed;
    string? selectedRun; // its activity is pushed with the state
    Dictionary<string, string?> installed; // where each CLI is; looked up again when the settings change

    Runner runner => tray.Runner;
    Platform platform => tray.Platform;

    public DashboardWindow(TrayApp tray)
    {
        this.tray = tray;
        installed = Installed();
        Title = "Butler";
        (Width, Height, MinWidth, MinHeight) = (1280, 800, 900, 560);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.App;
        Content = web;
        Closed += (_, _) => closed = true;
        web.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = Debugger.IsAttached;
            if (e is WindowsWebView2EnvironmentRequestedEventArgs w) w.UserDataFolder = Path.Combine(Program.Home, "webview");
        };
        web.WebMessageReceived += (_, e) => { if (e.Body is { } body) OnCommand(body); };
        web.NavigationCompleted += (_, _) => { ready = true; RefreshAll(); };
        // Links in Claude's output open in the browser; the dashboard itself never navigates away.
        web.NavigationStarted += (_, e) => { if (ready) { e.Cancel = true; OpenLink(e.Request?.AbsoluteUri); } };
        web.NewWindowRequested += (_, e) => { e.Handled = true; OpenLink(e.Request?.AbsoluteUri); };
        web.AdapterCreated += (_, _) => web.NavigateToString(Resource("dashboard.html") // once the engine is up
            .Replace("<!--marked-->", $"<script>{Resource("marked.min.js")}</script>")
            .Replace("<!--logo-->", Resource("butler.svg")).Replace("<!--version-->", Program.Version), new Uri("about:blank"));
    }

    static string Resource(string name)
    {
        using var s = typeof(DashboardWindow).Assembly.GetManifestResourceStream("Butler." + name)!;
        return new StreamReader(s).ReadToEnd();
    }

    static void OpenLink(string? uri)
    {
        if (uri != null && (uri.StartsWith("https://") || uri.StartsWith("http://"))) Open(uri);
    }

    public void RefreshAll()
    {
        installed = Installed();
        Push();
    }

    Dictionary<string, string?> Installed() => Cli.Names.ToDictionary(n => n, n => Cli.FromName(n).Find(platform.Shell));
    public void RefreshRuns() => Push();

    // Runs of the last 30 days and more, for the stats; read again at most once a minute (the page is pushed every 2 s).
    List<RunRecord> history = [];
    DateTime historyRead;

    List<RunRecord> History()
    {
        if (DateTime.Now - historyRead > TimeSpan.FromMinutes(1)) (history, historyRead) = (runner.Recent(1000), DateTime.Now);
        return history;
    }

    // The to-do list, loaded off the UI thread: when the dashboard opens, every minute while it is open, and after each edit.
    TodoList? todo;
    string? todoError;
    DateTime todoRead;
    bool todoLoading, todoAgain; // todoAgain: an edit ended during a load, which may have missed it

    void LoadTodo()
    {
        if (todoLoading) { todoAgain = true; return; }
        todoLoading = true;
        var settings = tray.Settings;
        Task.Run(async () => (TodoBackend.From(settings, platform.Secrets) is { } b ? await b.Load() : null)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            (todo, todoError) = t.IsCompletedSuccessfully ? (t.Result, null) : (todo, Error(t.Exception!));
            (todoRead, todoLoading) = (DateTime.Now, false);
            if (todoAgain) { todoAgain = false; LoadTodo(); }
            Push();
        }));
    }

    // Runs an edit of the to-do list, answers the page with its error (null when done) and what it returned, then reloads.
    void EditTodo(Func<TodoBackend, TodoList, Task<string?>> edit)
    {
        var settings = tray.Settings;
        var list = todo ?? new TodoList([], [], []);
        Task.Run(async () => TodoBackend.From(settings, platform.Secrets) is { } b ? await edit(b, list) : null).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            Call("todoDone", t.IsCompletedSuccessfully ? null : Error(t.Exception!), t.IsCompletedSuccessfully ? t.Result : null);
            LoadTodo();
        }));
    }

    static string Error(AggregateException e) => e.InnerException is TodoException or IOException or UnauthorizedAccessException
        ? e.InnerException.Message : e.InnerException?.ToString() ?? e.Message;

    static TodoTask Find(TodoList list, string id) =>
        list.Tasks.FirstOrDefault(t => t.Id == id) ?? throw new TodoException("This task is no longer in the list: reload and try again");

    void Push()
    {
        if (!ready || closed) return;
        var runs = runner.Recent(100);
        var settings = tray.Settings;
        if (settings.Todo != "none" && DateTime.Now - todoRead > TimeSpan.FromMinutes(1)) LoadTodo();
        TodoBackend? backend = null;
        try { backend = TodoBackend.From(settings, platform.Secrets); } catch (TodoException) { } // no token: the load says so
        var skills = Skills.Load(settings.SkillsFolder);
        var history = History();
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var state = new
        {
            error = tray.LastError,
            cli = settings.Cli,
            stats = new
            {
                waiting = runs.Count(r => r.Status == RunStatus.Waiting),
                running = runs.Count(r => r.Status == RunStatus.Running),
                today = history.Count(r => r.Started >= DateTime.Today),
                failedWeek = history.Count(r => r.Status == RunStatus.Failed && r.Started >= DateTime.Today.AddDays(-7)),
                costMonth = history.Where(r => r.Started >= monthStart).Sum(r => r.CostUsd ?? 0),
            },
            kinds = TriggerKind.All.Select(k => new { k.Name, k.Label, k.Variables, k.Form }),
            sources = TriggerKind.All.Where(k => tray.Triggers.Any(t => t.On == k.Name)).Select(k => new
            {
                k.Name, k.Label,
                count = tray.Triggers.Count(t => t.On == k.Name),
                @checked = tray.Sources.GetValueOrDefault(k.Name)?.Checked,
                error = tray.Sources.GetValueOrDefault(k.Name)?.Error,
            }),
            settings = new
            {
                settings.Cli, settings.TriggersFile, settings.SkillsFolder, settings.AdoOrganization, settings.Todo, settings.TodoFile, settings.StartPage,
                file = Program.SettingsFile, installed, todoist = platform.Secrets.Read(ISecrets.Todoist) != null, startup = platform.StartAtLogin.IsOn(Program.Exe),
                todos = TodoBackend.All.Select(b => new { b.Name, b.Label }),
            },
            todo = settings.Todo == "none" ? null : new
            {
                backend = settings.Todo, label = backend?.Label, notes = backend?.Notes, sections = backend?.Sections,
                addHint = backend?.AddHint, dateHint = backend?.DateHint,
                hidden = settings.TodoHidden, loading = todoLoading && todo == null, error = todoError, read = todoRead == DateTime.MinValue ? (DateTime?)null : todoRead,
                list = todo,
            },
            triggers = tray.Triggers.Select(t => new
            {
                t.Name, t.On, t.Prompt, t.Schedule, t.From,
                values = t.Kind.Form.ToDictionary(f => f.Name, f => f.Name == "schedule" ? t.Schedule : f.Name == "from" ? string.Join("\n", t.From ?? []) : t.Field(f.Name)),
                when = t.Kind.Describe(t),
                recent = history.Where(r => r.Trigger.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).Take(10).Select(r => r.Status),
                costMonth = history.Where(r => r.Started >= DateTime.Today.AddDays(-30) && r.Trigger.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).Sum(r => r.CostUsd ?? 0),
                skill = Skills.Invoked(t.Prompt),
                skillKnown = Skills.Invoked(t.Prompt) is not { } s || s.Contains(':') || skills.Any(k => k.Name == s || k.Folder == s),
                next = t.On == "schedule" ? Schedule.Parse(t.Schedule!).Next(DateTime.Now) : (DateTime?)null,
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
        _ = web.InvokeScript($"receive({JsonSerializer.Serialize(state, Json)})");
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
            case "clearRuns": runner.Clear(); historyRead = DateTime.MinValue; Push(); break;
            case "editConfig": Open(tray.Settings.TriggersFile); break;
            case "openSkills": platform.Shell.OpenFolder(tray.Settings.SkillsFolder); break;
            case "openSkill": Open(Path.Combine(tray.Settings.SkillsFolder, Arg("folder"), "SKILL.md")); break;
            case "openLog": Open(Path.Combine(runner.RunsDir, Arg("id") + ".log")); break;
            // Edits the triggers and skills; the file watcher then syncs and refreshes this page.
            case "chat": platform.Shell.Terminal(tray.Settings.SkillsFolder, runner.Cli.Exe, runner.Cli.TerminalArguments(null)); break;
            case "terminal":
                var cli = Cli.FromName(Arg("cli"));
                platform.Shell.Terminal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cli.Exe, cli.TerminalArguments(Arg("session")));
                break;
            case "saveSettings":
                var settings = tray.Settings with // what the page does not edit (hidden lists) is kept
                {
                    Cli = Arg("cli"), TriggersFile = Arg("triggersFile").Trim(), SkillsFolder = Arg("skillsFolder").Trim(),
                    AdoOrganization = Arg("adoOrganization").Trim(), Todo = Arg("todo"), TodoFile = Arg("todoFile").Trim(), StartPage = Arg("startPage"),
                };
                var problems = settings.Problems(platform.Shell);
                var token = Arg("todoistToken").Trim(); // empty: keep the saved one
                if (settings.Todo == "todoist")
                {
                    if (token == "" && platform.Secrets.Read(ISecrets.Todoist) == null) problems["todoistToken"] = "Paste your Todoist API token";
                    else if (token != "" && Task.Run(() => new Todoist(token).Check()).ContinueWith(t => t.Exception?.InnerException?.Message).Result is { } refused)
                        problems["todoistToken"] = refused;
                }
                if (problems.Count == 0)
                {
                    if (token != "") platform.Secrets.Write(ISecrets.Todoist, token);
                    platform.StartAtLogin.Set(cmd.GetProperty("startup").GetBoolean(), Program.Exe);
                    settings.Save(Program.SettingsFile);
                    tray.Reload(); // also done by the settings watcher, a second later
                    (todo, todoError, todoRead) = (null, null, DateTime.MinValue);
                }
                Call("settingsSaved", problems);
                break;
            case "forgetTodoist":
                platform.Secrets.Delete(ISecrets.Todoist);
                break;
            case "todoOpen":
                if (tray.Settings.Todo == "todoist") Open("https://app.todoist.com/");
                else if (Environment.ExpandEnvironmentVariables(tray.Settings.TodoFile) is var file && File.Exists(file)) platform.Shell.Reveal(file);
                else platform.Shell.OpenFolder(Path.GetDirectoryName(file)!); // not created yet
                break;
            case "todoRefresh": todoRead = DateTime.MinValue; LoadTodo(); break;
            case "todoAdd": EditTodo(async (b, _) => { await b.Add(Arg("text")); return null; }); break;
            case "todoSave":
                var edit = new TodoEdit(Arg("content"), Arg("description"), Arg("due"), cmd.GetProperty("priority").GetInt32(), Arg("projectId"),
                    cmd.GetProperty("sectionId").GetString() is { Length: > 0 } section ? section : null);
                EditTodo(async (b, list) => { await b.Save(Find(list, Arg("id")), edit); return null; });
                break;
            case "todoComplete": EditTodo(async (b, list) => await b.Complete(Find(list, Arg("id")))); break;
            case "todoReopen": EditTodo(async (b, _) => { await b.Reopen(Arg("id")); return null; }); break;
            case "todoDelete": EditTodo(async (b, list) => { await b.Delete(Find(list, Arg("id"))); return null; }); break;
            case "todoPlace":
                var (to, toSection) = (cmd.GetProperty("index").GetInt32(), cmd.GetProperty("sectionId").GetString() is { Length: > 0 } ts ? ts : null);
                EditTodo(async (b, list) => { await b.Place(list, Find(list, Arg("id")), Arg("projectId"), toSection, to); return null; });
                break;
            case "todoInsert":
                var (index, sectionId) = (cmd.GetProperty("index").GetInt32(), cmd.GetProperty("sectionId").GetString() is { Length: > 0 } s ? s : null);
                EditTodo(async (b, list) => { await b.Insert(list, Arg("text"), Arg("projectId"), sectionId, index); return null; });
                break;
            case "todoHide":
                var hidden = tray.Settings.TodoHidden.Where(id => id != Arg("id"));
                (tray.Settings with { TodoHidden = [.. cmd.GetProperty("hide").GetBoolean() ? hidden.Append(Arg("id")) : hidden] }).Save(Program.SettingsFile);
                tray.Reload();
                break;
            case "browse":
                _ = Browse(Arg("pick") == "folder", Arg("title"), Arg("value"), cmd.TryGetProperty("filter", out var filter) ? filter.GetString()! : "All files|*.*", Arg("target"));
                break;
            case "saveTrigger":
                Call("triggerSaved", EditTriggers(list =>
                {
                    var values = cmd.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                    var t = Trigger.FromForm(Arg("name"), Arg("on"), Arg("prompt"), values);
                    var at = list.FindIndex(x => x.Name.Equals(Arg("original"), StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) list[at] = t;
                    else list.Add(t);
                }));
                break;
            case "deleteTrigger":
                Call("triggerSaved", EditTriggers(list => list.RemoveAll(x => x.Name.Equals(Arg("name"), StringComparison.OrdinalIgnoreCase))));
                break;
        }
        Push();
    }

    // Changes the triggers file as it is on disk (not the tray's copy: a hand edit is not lost), then reloads.
    // Returns the error, or null when saved.
    string? EditTriggers(Action<List<Trigger>> change)
    {
        try
        {
            var path = tray.Settings.TriggersFile;
            var list = Trigger.Load(path);
            change(list);
            Trigger.Save(path, list);
            tray.Reload(); // also done by the file watcher, a second later
            return null;
        }
        catch (Exception e) when (e is FormatException or IOException or JsonException) { return e.Message; }
    }

    // Picks a file or folder for a path field and gives it to the page (nothing when cancelled).
    // filter: "Text (*.txt)|*.txt|All files|*.*", name|patterns pairs, patterns separated by ;.
    async Task Browse(bool folder, string title, string current, string filter, string target)
    {
        current = Environment.ExpandEnvironmentVariables(current);
        var dir = folder ? current : Path.GetDirectoryName(current);
        var start = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(dir) ? dir : Program.Home);
        IStorageItem? picked = folder
            ? (await StorageProvider.OpenFolderPickerAsync(new() { Title = title, SuggestedStartLocation = start })).FirstOrDefault()
            : (await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = title, SuggestedStartLocation = start, SuggestedFileName = Path.GetFileName(current),
                FileTypeFilter = [.. filter.Split('|').Chunk(2).Select(p => new FilePickerFileType(p[0]) { Patterns = p[1].Split(';') })],
            })).FirstOrDefault();
        if (picked?.TryGetLocalPath() is { } path) Call("picked", target, path);
    }

    // Calls a function of the page with JSON arguments (the answer to a command).
    void Call(string function, params object?[] args) =>
        _ = web.InvokeScript($"{function}({string.Join(",", args.Select(a => JsonSerializer.Serialize(a, Json)))})");

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
