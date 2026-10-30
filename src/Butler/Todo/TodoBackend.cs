namespace Butler;

public sealed record TodoProject(string Id, string Name);
public sealed record TodoSection(string Id, string ProjectId, string Name);
// Priority: 4 = most urgent (Todoist's p1) to 1 = none. Due: yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss; DueText: as the user wrote it.
public sealed record TodoTask(string Id, string Content, string Description, string ProjectId, string? SectionId, string? ParentId,
    int Priority, int Order, string? Due, string? DueText, bool Recurring);
public sealed record TodoList(List<TodoProject> Projects, List<TodoSection> Sections, List<TodoTask> Tasks);
// What the dashboard's task editor sends: Due is free text, read by the backend ("" removes the date).
public sealed record TodoEdit(string Content, string Description, string Due, int Priority, string ProjectId, string? SectionId);

// An error to show as is (bad token, unknown date, the file changed...).
public sealed class TodoException(string message) : Exception(message);

// Where the to-do list lives, chosen on the Settings page ("todo"): Todoist, or a todo.txt file. Edits are made by hand
// on the dashboard; agents reach the same list through their own tools.
public abstract class TodoBackend
{
    public static readonly (string Name, string Label)[] All = [("none", "None"), ("todoist", "Todoist"), ("txt", "todo.txt file")];

    // null when no to-do list is set.
    public static TodoBackend? From(Settings settings) => settings.Todo switch
    {
        "todoist" => new Todoist(Secrets.Read(Secrets.Todoist) is { Length: > 0 } token ? token
            : throw new TodoException("No Todoist token: paste it on the Settings page")),
        "txt" => new TodoTxt(Environment.ExpandEnvironmentVariables(settings.TodoFile)),
        _ => null,
    };

    public abstract string Label { get; }
    public abstract bool Notes { get; }        // tasks have a description
    public abstract bool Sections { get; }     // projects have sections
    public abstract string AddHint { get; }    // placeholder of the quick add box
    public abstract string DateHint { get; }

    public abstract Task<TodoList> Load();
    public abstract Task Add(string text);
    public abstract Task Save(TodoTask task, TodoEdit edit);
    public abstract Task<string> Complete(TodoTask task); // returns the id that Reopen takes (undo)
    public abstract Task Reopen(string id);
    public abstract Task Delete(TodoTask task);

    // Puts these tasks (siblings) in this order.
    public abstract Task Reorder(IReadOnlyList<TodoTask> siblings);

    // Adds a task in a list (and section) at a place: index among its top-level tasks, as Siblings lists them.
    public abstract Task Insert(TodoList list, string text, string projectId, string? sectionId, int index);

    // The tasks shown together with this one (same list, section and parent), in their order.
    public static List<TodoTask> Siblings(TodoList list, string projectId, string? sectionId, string? parentId) =>
        list.Tasks.Where(t => t.ProjectId == projectId && t.SectionId == sectionId && t.ParentId == parentId).OrderBy(t => t.Order).ToList();

    // Moves a task to another list or section, as a top-level task; returns it as it is now (todo.txt: its new line).
    public abstract Task<TodoTask> MoveTo(TodoTask task, string projectId, string? sectionId);

    // Puts a task where it was dropped: in a list and section, at an index among its siblings there (without it).
    // In its own list and section it keeps its parent, and nothing is written when it is already there.
    public async Task Place(TodoList list, TodoTask task, string projectId, string? sectionId, int index)
    {
        var moving = task.ProjectId != projectId || task.SectionId != sectionId;
        if (moving)
        {
            var moved = await MoveTo(task, projectId, sectionId);
            list = list with { Tasks = [.. list.Tasks.Where(t => t.Id != task.Id), moved] };
            task = moved;
        }
        var siblings = Siblings(list, projectId, sectionId, task.ParentId);
        var before = siblings.Select(t => t.Id).ToList();
        siblings.RemoveAll(t => t.Id == task.Id);
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), task);
        if (moving || !siblings.Select(t => t.Id).SequenceEqual(before)) await Reorder(siblings);
    }
}
