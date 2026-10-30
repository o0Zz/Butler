using System.Text.Json;

namespace Butler;

// Remembers the last event handled (time + the ids at that time), so a poll, or a start after downtime,
// only sees events not handled yet. A new checkpoint starts from now: old events are not replayed.
public sealed class Checkpoint
{
    public DateTime Last { get; set; } = DateTime.Now;
    public List<string> IdsAtLast { get; set; } = [];

    public static T Load<T>(string path) where T : new() =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path))! : new T();

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    // Takes the events at or after Last, returns the new ones oldest first and moves the checkpoint.
    public List<T> TakeNew<T>(IEnumerable<T> events, Func<T, string> id, Func<T, DateTime> at)
    {
        var fresh = events
            .Where(e => at(e) > Last || (at(e) == Last && !IdsAtLast.Contains(id(e))))
            .OrderBy(at)
            .ToList();
        if (fresh.Count == 0) return fresh;

        var newest = at(fresh[^1]);
        IdsAtLast = newest == Last ? [.. IdsAtLast, .. fresh.Where(e => at(e) == newest).Select(id)]
                                   : [.. fresh.Where(e => at(e) == newest).Select(id)];
        Last = newest;
        return fresh;
    }
}
