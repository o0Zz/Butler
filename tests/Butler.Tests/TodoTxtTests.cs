namespace Butler.Tests;

public class TodoTxtTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("butler-todo-").FullName;
    string File_ => Path.Combine(dir, "todo.txt");
    static readonly DateTime Today = new(2026, 10, 30); // a Friday
    TodoTxt List() => new(File_, () => Today);

    [Fact]
    public async Task Loads_open_tasks_with_their_priority_project_and_date()
    {
        File.WriteAllText(File_, "(A) 2026-10-01 Call Lena +Work due:2026-11-03\r\nx 2026-10-29 Done already +Work\r\n\r\nBuy milk\r\n(C) Fix bike +Home\r\n");
        var list = await List().Load();

        Assert.Equal(["Inbox", "Work", "Home"], list.Projects.Select(p => p.Name));
        Assert.Equal(["Call Lena", "Buy milk", "Fix bike"], list.Tasks.Select(t => t.Content));
        var lena = list.Tasks[0];
        Assert.Equal((4, "Work", "2026-11-03"), (lena.Priority, lena.ProjectId, lena.Due));
        Assert.Equal((1, TodoTxt.Inbox, (string?)null), (list.Tasks[1].Priority, list.Tasks[1].ProjectId, list.Tasks[1].Due));
        Assert.Equal(2, list.Tasks[2].Priority);
    }

    [Fact]
    public async Task Edits_write_the_todo_txt_format_and_keep_crlf()
    {
        File.WriteAllText(File_, "(A) 2026-10-01 Call Lena +Work\r\n");
        var todo = List();
        await todo.Add("Buy milk");
        var lena = (await todo.Load()).Tasks[0];
        await todo.Save(lena, new TodoEdit("Call Lena back", "", "tomorrow", 3, "Home", null));
        Assert.Equal("(B) 2026-10-01 Call Lena back +Home due:2026-10-31\r\nBuy milk\r\n", File.ReadAllText(File_));

        var done = await todo.Complete((await todo.Load()).Tasks[0]);
        Assert.Equal("x 2026-10-30 2026-10-01 Call Lena back +Home due:2026-10-31 pri:B\r\nBuy milk\r\n", File.ReadAllText(File_));
        Assert.Equal(["Buy milk"], (await todo.Load()).Tasks.Select(t => t.Content));

        await todo.Reopen(done); // undo
        Assert.Equal("(B) 2026-10-01 Call Lena back +Home due:2026-10-31\r\nBuy milk\r\n", File.ReadAllText(File_));

        await todo.Delete((await todo.Load()).Tasks[1]);
        Assert.Equal("(B) 2026-10-01 Call Lena back +Home due:2026-10-31\r\n", File.ReadAllText(File_));
    }

    [Fact]
    public async Task A_new_file_is_created_with_the_first_task()
    {
        await List().Add("Buy milk");
        Assert.Equal("Buy milk\r\n", File.ReadAllText(File_));
    }

    [Fact]
    public async Task A_task_changed_in_the_file_meanwhile_is_not_overwritten()
    {
        File.WriteAllText(File_, "Buy milk\n");
        var todo = List();
        var task = (await todo.Load()).Tasks[0];
        File.WriteAllText(File_, "Buy milk and eggs\n"); // edited by hand or by an agent
        Assert.Contains("changed in the file", (await Assert.ThrowsAsync<TodoException>(() => todo.Complete(task))).Message);
        Assert.Equal("Buy milk and eggs\n", File.ReadAllText(File_));
    }

    [Fact]
    public async Task A_dropped_task_takes_its_place_among_the_tasks_of_its_list()
    {
        File.WriteAllText(File_, "A +Work\nB +Home\nC +Work\nD +Work\n");
        var todo = List();
        var list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "D"), "Work", null, 0);
        Assert.Equal("D +Work\nB +Home\nA +Work\nC +Work\n", File.ReadAllText(File_)); // Home's line stays where it is

        list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "D"), "Work", null, 2); // to the end (index in the list without it)
        Assert.Equal("A +Work\nB +Home\nC +Work\nD +Work\n", File.ReadAllText(File_));

        File.SetLastWriteTime(File_, new DateTime(2020, 1, 1));
        list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "A"), "Work", null, 0); // already there
        await todo.Place(list, list.Tasks.Single(t => t.Content == "B"), "Home", null, 5); // alone in its list
        Assert.Equal(new DateTime(2020, 1, 1), File.GetLastWriteTime(File_)); // not even rewritten
    }

    [Fact]
    public async Task A_task_dropped_in_another_list_changes_its_tag_and_takes_its_place()
    {
        File.WriteAllText(File_, "(A) Call Lena due:2026-11-03\nB +Home\nC +Work @phone\nD +Work\n");
        var todo = List();
        var list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "Call Lena"), "Work", null, 1); // from the Inbox, between C and D
        Assert.Equal("C +Work @phone\nB +Home\n(A) Call Lena due:2026-11-03 +Work\nD +Work\n", File.ReadAllText(File_)); // Work's lines take the new order

        list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "C @phone"), "Home", null, 0); // the first +tag is replaced
        Assert.Equal("C +Home @phone\nB +Home\n(A) Call Lena due:2026-11-03 +Work\nD +Work\n", File.ReadAllText(File_));

        list = await todo.Load();
        await todo.Place(list, list.Tasks.Single(t => t.Content == "D"), TodoTxt.Inbox, null, 0); // to the Inbox: no tag
        Assert.Equal("C +Home @phone\nB +Home\n(A) Call Lena due:2026-11-03 +Work\nD\n", File.ReadAllText(File_));
    }

    [Fact]
    public async Task A_task_is_inserted_at_the_place_clicked_in_its_list()
    {
        File.WriteAllText(File_, "A +Work\nB +Home\nC +Work\n");
        var todo = List();
        await todo.Insert(await todo.Load(), "First", "Work", null, 0);
        await todo.Insert(await todo.Load(), "Between", "Work", null, 2);
        await todo.Insert(await todo.Load(), "Last +Work", "Work", null, 4);
        await todo.Insert(await todo.Load(), "In inbox", TodoTxt.Inbox, null, 0);
        Assert.Equal("First +Work\nA +Work\nB +Home\nBetween +Work\nC +Work\nLast +Work\nIn inbox\n", File.ReadAllText(File_));
    }

    [Theory]
    [InlineData("today", "2026-10-30")]
    [InlineData("Tomorrow", "2026-10-31")]
    [InlineData("next week", "2026-11-06")]
    [InlineData("monday", "2026-11-02")]
    [InlineData("fri", "2026-11-06")] // the next one, never today
    [InlineData("2026-12-24", "2026-12-24")]
    [InlineData("", null)]
    public void Parses_dates(string text, string? date) => Assert.Equal(date, TodoTxt.ParseDate(text, Today));

    [Fact]
    public void Refuses_an_unknown_date() => Assert.Throws<TodoException>(() => TodoTxt.ParseDate("soon", Today));

    public void Dispose() => Directory.Delete(dir, true);
}
