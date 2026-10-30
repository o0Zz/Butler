using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Butler.Tests;

// Todoist against a local HTTP server answering as its API v1 does, recording what Butler sends.
public class TodoistTests : IDisposable
{
    readonly HttpListener http = new();
    readonly string api;
    readonly List<string> requests = []; // "POST tasks/1 {body}"
    Func<string, (int Status, string Body)> answer = _ => (200, "{}");

    public TodoistTests()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        api = $"http://localhost:{port}/api/v1/";
        http.Prefixes.Add(api);
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                var ctx = await http.GetContextAsync();
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                if (ctx.Request.ContentType?.StartsWith("application/x-www-form-urlencoded") == true) body = WebUtility.UrlDecode(body);
                var path = ctx.Request.RawUrl!["/api/v1/".Length..];
                var line = $"{ctx.Request.HttpMethod} {path}" + (body.Length > 0 ? " " + body : "");
                lock (requests) requests.Add(line);
                var (status, text) = ctx.Request.Headers["Authorization"] == "Bearer secret" ? answer(line) : (401, "");
                ctx.Response.StatusCode = status;
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                ctx.Response.Close();
            }
        });
    }

    Todoist Client(string token = "secret") => new(token, api);

    static TodoTask Task_(string? dueText = null, string? section = null) =>
        new("1", "Call Lena", "", "p1", section, null, 1, 0, dueText == null ? null : "2026-11-03", dueText, false);

    [Fact]
    public async Task Loads_every_page_with_the_inbox_first_and_sub_projects_under_their_parent()
    {
        answer = line => line switch
        {
            "GET projects?limit=200" => (200, """{ "results": [ { "id": "w", "name": "Work", "child_order": 1 }, { "id": "i", "name": "Inbox", "inbox_project": true, "child_order": 9 } ], "next_cursor": "c2" }"""),
            "GET projects?limit=200&cursor=c2" => (200, """{ "results": [ { "id": "s", "name": "Sub", "parent_id": "w" }, { "id": "a", "name": "Old", "is_archived": true } ], "next_cursor": null }"""),
            "GET sections?limit=200" => (200, """{ "results": [ { "id": "x", "project_id": "w", "name": "Later", "section_order": 2 } ] }"""),
            "GET tasks?limit=200" => (200, """
                { "results": [
                  { "id": "1", "content": "Call Lena", "project_id": "w", "section_id": "x", "priority": 4, "child_order": 1,
                    "due": { "date": "2026-11-03", "string": "every monday", "is_recurring": true } },
                  { "id": "2", "content": "Buy milk", "project_id": "i", "priority": 1, "due": null } ] }
                """),
            _ => (404, ""),
        };
        var list = await Client().Load();

        Assert.Equal(["Inbox", "Work", "Sub"], list.Projects.Select(p => p.Name));
        Assert.Equal("Later", Assert.Single(list.Sections).Name);
        Assert.Equal(new TodoTask("1", "Call Lena", "", "w", "x", null, 4, 1, "2026-11-03", "every monday", true), list.Tasks[0]);
        Assert.Null(list.Tasks[1].Due);
    }

    [Fact]
    public async Task Save_sends_the_date_only_when_it_changed_and_moves_the_task()
    {
        await Client().Save(Task_("every monday"), new TodoEdit("Call Lena", "notes", "every monday", 4, "p1", null));
        await Client().Save(Task_("every monday"), new TodoEdit("Call Lena", "", "", 1, "p2", null));
        await Client().Save(Task_(), new TodoEdit("Call Lena", "", "tomorrow", 1, "p1", "s9"));

        Assert.Equal([
            """POST tasks/1 {"content":"Call Lena","description":"notes","priority":4}""", // a recurring date sent back could reset it
            """POST tasks/1 {"content":"Call Lena","description":"","priority":1,"due_string":"no date"}""",
            """POST tasks/1/move {"project_id":"p2"}""",
            """POST tasks/1 {"content":"Call Lena","description":"","priority":1,"due_string":"tomorrow"}""",
            """POST tasks/1/move {"section_id":"s9"}""",
        ], requests);
    }

    [Fact]
    public async Task Add_complete_reopen_and_delete_call_the_api()
    {
        var todo = Client();
        await todo.Add("Call Lena tomorrow #Work");
        Assert.Equal("1", await todo.Complete(Task_()));
        await todo.Reopen("1");
        await todo.Delete(Task_());
        Assert.Equal([
            """POST tasks/quick {"text":"Call Lena tomorrow #Work"}""", "POST tasks/1/close", "POST tasks/1/reopen", "DELETE tasks/1",
        ], requests);
    }

    // The Sync API answers each command by its uuid: "ok" or an error object.
    static (int, string) SyncAnswer(string line, string status) =>
        (200, $$"""{ "sync_status": { "{{Regex.Match(line, "\"uuid\":\"([^\"]+)\"").Groups[1].Value}}": {{status}} } }""");

    static TodoList Work() => new([new("w", "Work")], [], [
        new("a", "A", "", "w", null, null, 1, 5, null, null, false),
        new("b", "B", "", "w", null, null, 1, 7, null, null, false),
        new("x", "In a section", "", "w", "s", null, 1, 6, null, null, false),
        new("c", "C", "", "w", null, null, 1, 9, null, null, false),
    ]);

    [Fact]
    public async Task A_dropped_task_reorders_its_siblings_with_the_sync_api()
    {
        answer = line => line.StartsWith("POST sync") ? SyncAnswer(line, "\"ok\"") : (404, "");
        var list = Work();
        await Client().Place(list, list.Tasks[3], 1); // c between a and b (the task in a section is not a sibling)
        await Client().Place(list, list.Tasks[0], 0); // already first: nothing sent
        var sent = Assert.Single(requests);
        Assert.StartsWith("""POST sync commands=[{"type":"item_reorder",""", sent);
        Assert.Contains("""{"items":[{"id":"a","child_order":1},{"id":"c","child_order":2},{"id":"b","child_order":3}]}""", sent);

        answer = line => SyncAnswer(line, """{ "error": "Item not found" }""");
        Assert.Contains("Item not found", (await Assert.ThrowsAsync<TodoException>(() => Client().Place(list, list.Tasks[1], 2))).Message);
    }

    [Fact]
    public async Task Inserting_quick_adds_then_moves_the_task_to_its_place()
    {
        answer = line => line.StartsWith("POST tasks/quick") ? (200, """{ "id": "n" }""") : line.StartsWith("POST sync") ? SyncAnswer(line, "\"ok\"") : (200, "{}");
        await Client().Insert(Work(), "Call Lena tomorrow", "w", null, 1);
        Assert.Equal("""POST tasks/quick {"text":"Call Lena tomorrow"}""", requests[0]);
        Assert.Equal("""POST tasks/n/move {"project_id":"w"}""", requests[1]);
        Assert.Contains("""{"items":[{"id":"a","child_order":1},{"id":"n","child_order":2},{"id":"b","child_order":3},{"id":"c","child_order":4}]}""", requests[2]);
    }

    [Fact]
    public async Task A_refused_token_or_an_api_error_says_what_to_do()
    {
        Assert.Contains("refused the token", (await Assert.ThrowsAsync<TodoException>(() => Client("wrong").Check())).Message);
        answer = _ => (400, """{ "error": "Invalid date" }""");
        Assert.Contains("Invalid date", (await Assert.ThrowsAsync<TodoException>(() => Client().Add("x"))).Message);
        Assert.Contains("cannot be reached", (await Assert.ThrowsAsync<TodoException>(() => new Todoist("secret", "http://localhost:1/").Check())).Message);
    }

    public void Dispose() => http.Close();
}
