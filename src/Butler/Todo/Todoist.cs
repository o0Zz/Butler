using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Butler;

// Todoist through its API v1 (https://developer.todoist.com/api/v1/), with the user's personal token.
public sealed class Todoist(string token, string api = "https://api.todoist.com/api/v1/") : TodoBackend
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public override string Label => "Todoist";
    public override bool Notes => true;
    public override bool Sections => true;
    public override string AddHint => T("Add a task: Call Lena tomorrow 9am #Work p1");
    public override string DateHint => T("In words, as in Todoist: tomorrow 9am, every monday, Nov 3. Empty: no date.");

    // Fails with a TodoException when the token is refused.
    public Task Check() => Send(HttpMethod.Get, "projects?limit=1");

    public override async Task<TodoList> Load()
    {
        var projects = await Pages("projects");
        var sections = await Pages("sections");
        var tasks = await Pages("tasks");
        return new TodoList(
            Tree(projects.Where(p => !Bool(p, "is_archived")).ToList()),
            sections.OrderBy(s => Int(s, "section_order")).Select(s => new TodoSection(Str(s, "id")!, Str(s, "project_id")!, Str(s, "name")!)).ToList(),
            tasks.Select(t => t.TryGetProperty("due", out var due) && due.ValueKind == JsonValueKind.Object
                ? ToTask(t, Str(due, "date"), Str(due, "string"), Bool(due, "is_recurring"))
                : ToTask(t, null, null, false)).ToList());
    }

    static TodoTask ToTask(JsonElement t, string? due, string? dueText, bool recurring) => new(
        Str(t, "id")!, Str(t, "content") ?? "", Str(t, "description") ?? "", Str(t, "project_id")!, Str(t, "section_id"), Str(t, "parent_id"),
        Int(t, "priority"), Int(t, "child_order"), due, dueText, recurring);

    // Inbox first, then the projects as in Todoist's sidebar: each one followed by its sub-projects.
    static List<TodoProject> Tree(List<JsonElement> projects)
    {
        var result = new List<TodoProject>();
        void Add(string? parent) =>
            projects.Where(p => Str(p, "parent_id") == parent).OrderBy(p => Inbox(p) ? 0 : 1).ThenBy(p => Int(p, "child_order")).ToList().ForEach(p =>
            {
                result.Add(new TodoProject(Str(p, "id")!, Str(p, "name")!));
                Add(Str(p, "id"));
            });
        Add(null);
        return result;
    }

    static bool Inbox(JsonElement p) => Bool(p, "inbox_project") || Bool(p, "is_inbox_project");

    public override Task Add(string text) => Send(HttpMethod.Post, "tasks/quick", new { text });

    public override async Task Save(TodoTask task, TodoEdit edit)
    {
        var body = new Dictionary<string, object> { ["content"] = edit.Content, ["description"] = edit.Description, ["priority"] = edit.Priority };
        // Sent only when changed: sending a recurring date back could reset its recurrence.
        if (edit.Due.Trim() != (task.DueText ?? "")) body["due_string"] = edit.Due.Trim() is { Length: > 0 } due ? due : "no date";
        await Send(HttpMethod.Post, $"tasks/{task.Id}", body);
        if (edit.ProjectId != task.ProjectId || edit.SectionId != task.SectionId)
            await Send(HttpMethod.Post, $"tasks/{task.Id}/move", edit.SectionId != null ? new { section_id = edit.SectionId } : new { project_id = edit.ProjectId });
    }

    public override async Task<string> Complete(TodoTask task)
    {
        await Send(HttpMethod.Post, $"tasks/{task.Id}/close");
        return task.Id;
    }

    public override Task Reopen(string id) => Send(HttpMethod.Post, $"tasks/{id}/reopen");
    public override Task Delete(TodoTask task) => Send(HttpMethod.Delete, $"tasks/{task.Id}");

    public override async Task<TodoTask> MoveTo(TodoTask task, string projectId, string? sectionId)
    {
        await Send(HttpMethod.Post, $"tasks/{task.Id}/move", sectionId != null ? new { section_id = sectionId } : new { project_id = projectId });
        return task with { ProjectId = projectId, SectionId = sectionId, ParentId = null };
    }

    // Through the Sync API's item_reorder: child_order 1..n.
    public override async Task Reorder(IReadOnlyList<TodoTask> siblings)
    {
        var command = new { type = "item_reorder", uuid = Guid.NewGuid().ToString(), args = new { items = siblings.Select((t, i) => new { id = t.Id, child_order = i + 1 }) } };
        var result = await Send(HttpMethod.Post, "sync", new FormUrlEncodedContent([new("commands", JsonSerializer.Serialize(new[] { command }))]));
        if (result.GetProperty("sync_status").GetProperty(command.uuid) is { ValueKind: not JsonValueKind.String } error)
            throw new TodoException("Todoist refused the new order: " + (Str(error, "error") ?? error.GetRawText()));
    }

    // Quick add (dates, labels, priority are read from the text), then moved and reordered to its place.
    public override async Task Insert(TodoList list, string text, string projectId, string? sectionId, int index)
    {
        var id = Str(await Send(HttpMethod.Post, "tasks/quick", new { text }), "id")!;
        await Send(HttpMethod.Post, $"tasks/{id}/move", sectionId != null ? new { section_id = sectionId } : new { project_id = projectId });
        var siblings = Siblings(list, projectId, sectionId, null);
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), new TodoTask(id, text, "", projectId, sectionId, null, 1, 0, null, null, false));
        await Reorder(siblings);
    }

    // Every page of a list (cursor pagination).
    async Task<List<JsonElement>> Pages(string path)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await Send(HttpMethod.Get, $"{path}?limit=200" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            all.AddRange(page.GetProperty("results").EnumerateArray());
            cursor = Str(page, "next_cursor");
        } while (cursor != null);
        return all;
    }

    async Task<JsonElement> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, api + path);
        request.Headers.Authorization = new("Bearer", token);
        if (body != null) request.Content = body as HttpContent ?? JsonContent.Create(body);
        try
        {
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TodoException("Todoist refused the token: paste a new one on the Settings page");
            if (!response.IsSuccessStatusCode) throw new TodoException($"Todoist answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 200)]}");
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new TodoException("Todoist cannot be reached: " + e.Message);
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
