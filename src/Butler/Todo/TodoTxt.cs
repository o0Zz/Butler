using System.Globalization;
using System.Text.RegularExpressions;

namespace Butler;

// A todo.txt file (http://todotxt.org): one task per line, "(A) Call Lena +Work due:2026-11-03", done tasks start with "x ".
// The project is the first +tag (none: the Inbox). A task's id is its line: an edit fails, and writes nothing, when that
// line is no longer in the file (changed meanwhile by hand or by an agent). Line endings are kept.
public sealed class TodoTxt(string path, Func<DateTime>? today = null) : TodoBackend
{
    public const string Inbox = "inbox";
    static readonly Regex Head = new(@"^(?:\((?<pri>[A-Z])\) )?(?:(?<created>\d{4}-\d{2}-\d{2}) )?(?<text>.*)$");
    static readonly Regex Project = new(@"(?<=^|\s)\+(\S+)");
    static readonly Regex DueTag = new(@"(?<=^|\s)due:(\d{4}-\d{2}-\d{2})(?=\s|$)");
    static readonly Regex Done = new(@"^x (?:\d{4}-\d{2}-\d{2} )?(?<rest>.*?)(?: pri:(?<pri>[A-Z]))?$");

    DateTime Today => (today ?? (() => DateTime.Today))();

    public override string Label => "todo.txt";
    public override bool Notes => false;
    public override bool Sections => false;
    public override string AddHint => "Add a task: (A) Call Lena +Work due:2026-11-03";
    public override string DateHint => "today, tomorrow, next week, a weekday (monday) or 2026-11-03. Empty: no date.";

    public override Task<TodoList> Load()
    {
        var tasks = Lines().Select((line, i) => (line, i)).Where(x => x.line.Trim().Length > 0 && !x.line.StartsWith("x "))
            .Select(x => Parse(x.line, x.i)).ToList();
        var projects = tasks.Select(t => t.ProjectId).Where(p => p != Inbox).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new TodoProject(p, p)).Prepend(new TodoProject(Inbox, "Inbox")).ToList();
        return Task.FromResult(new TodoList(projects, [], tasks));
    }

    static TodoTask Parse(string line, int order)
    {
        var m = Head.Match(line.Trim());
        var text = m.Groups["text"].Value;
        var project = Project.Match(text);
        var due = DueTag.Match(text);
        var content = text;
        if (due.Success) content = content.Remove(due.Index, due.Length);
        if (project.Success) content = Project.Replace(content, "", 1);
        var pri = m.Groups["pri"].Success ? m.Groups["pri"].Value[0] : ' ';
        return new TodoTask(line, Regex.Replace(content, @"\s+", " ").Trim(), "", project.Success ? project.Groups[1].Value : Inbox, null, null,
            pri switch { 'A' => 4, 'B' => 3, 'C' => 2, _ => 1 }, order, due.Success ? due.Groups[1].Value : null, due.Success ? due.Groups[1].Value : null, false);
    }

    public override Task Add(string text) => Change(lines => lines.Add(text.Trim()));

    public override Task Save(TodoTask task, TodoEdit edit)
    {
        var due = ParseDate(edit.Due, Today);
        var created = Head.Match(task.Id.Trim()).Groups["created"];
        var line = (edit.Priority switch { 4 => "(A) ", 3 => "(B) ", 2 => "(C) ", _ => "" }) + (created.Success ? created.Value + " " : "")
            + edit.Content.Trim() + (edit.ProjectId != Inbox ? " +" + edit.ProjectId : "") + (due != null ? " due:" + due : "");
        return Change(lines => lines[Find(lines, task.Id)] = line);
    }

    // Completed as the format says: "x <date> ", the priority kept as pri:A.
    public override async Task<string> Complete(TodoTask task)
    {
        var m = Head.Match(task.Id.Trim());
        var done = $"x {Today:yyyy-MM-dd} " + (m.Groups["created"].Success ? m.Groups["created"].Value + " " : "") + m.Groups["text"].Value
            + (m.Groups["pri"].Success ? " pri:" + m.Groups["pri"].Value : "");
        await Change(lines => lines[Find(lines, task.Id)] = done);
        return done;
    }

    public override Task Reopen(string id)
    {
        var m = Done.Match(id);
        return Change(lines => lines[Find(lines, id)] = (m.Groups["pri"].Success ? $"({m.Groups["pri"].Value}) " : "") + m.Groups["rest"].Value);
    }

    public override Task Delete(TodoTask task) => Change(lines => lines.RemoveAt(Find(lines, task.Id)));

    // Its first +tag is its list: replaced, added, or removed for the Inbox. The rest of the line is kept.
    public override async Task<TodoTask> MoveTo(TodoTask task, string projectId, string? sectionId)
    {
        var line = projectId == Inbox ? Regex.Replace(Project.Replace(task.Id, "", 1), @"\s{2,}", " ").Trim()
            : Project.IsMatch(task.Id) ? Project.Replace(task.Id, "+" + projectId, 1) : task.Id.TrimEnd() + " +" + projectId;
        await Change(lines => lines[Find(lines, task.Id)] = line);
        return Parse(line, task.Order);
    }

    // The order is the lines' order: the tasks swap the lines they hold.
    public override Task Reorder(IReadOnlyList<TodoTask> siblings) => Change(lines =>
    {
        var slots = siblings.Select(t => Find(lines, t.Id)).Order().ToList();
        for (var i = 0; i < slots.Count; i++) lines[slots[i]] = siblings[i].Id;
    });

    // A line before the task at that index, after the last one, or at the end of the file in an empty list.
    public override Task Insert(TodoList list, string text, string projectId, string? sectionId, int index)
    {
        var line = text.Trim() + (projectId != Inbox && !Project.IsMatch(text) ? " +" + projectId : "");
        var siblings = Siblings(list, projectId, null, null);
        return Change(lines =>
        {
            if (siblings.Count == 0) lines.Add(line);
            else if (index < siblings.Count) lines.Insert(Find(lines, siblings[Math.Max(index, 0)].Id), line);
            else lines.Insert(Find(lines, siblings[^1].Id) + 1, line);
        });
    }

    // A date typed in the task editor, as yyyy-MM-dd; null for none.
    public static string? ParseDate(string text, DateTime today)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t == "") return null;
        DateTime? day = t switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "next week" => today.AddDays(7),
            _ => null,
        };
        var weekday = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>().FirstOrDefault(d => d.ToString()!.ToLowerInvariant() is var n && (n == t || n[..3] == t));
        if (weekday != null) day = today.AddDays(((int)weekday - (int)today.DayOfWeek + 6) % 7 + 1); // the next one, never today
        if (day == null && DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) day = d;
        return day?.ToString("yyyy-MM-dd") ?? throw new TodoException($"Unknown date '{text}': use today, tomorrow, next week, a weekday or 2026-11-03");
    }

    static int Find(List<string> lines, string id)
    {
        var i = lines.IndexOf(id);
        return i >= 0 ? i : throw new TodoException("This task changed in the file meanwhile: the list is reloaded, try again");
    }

    List<string> Lines() => File.Exists(path) ? [.. File.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r'))] : [];

    // Reads the file, applies the change, writes it back with its line endings (the OS's for a new file).
    Task Change(Action<List<string>> change)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        var lines = Lines();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1); // the file's last newline
        change(lines);
        File.WriteAllText(path + ".tmp", string.Join(nl, lines) + (lines.Count > 0 ? nl : ""));
        File.Move(path + ".tmp", path, overwrite: true);
        return Task.CompletedTask;
    }
}
