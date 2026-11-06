using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Butler;

// One entry of the triggers file. "on" names its kind (TriggerKind.All). Schedule and From are the fields of "schedule"
// and "mail"; the fields of the other kinds land in Fields. It runs either a prompt (the agent) or a command (the shell).
public sealed record Trigger(string Name, string On, string? Prompt, string? Schedule = null, string[]? From = null)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions WriteJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // keeps accents readable
    public static readonly string[] Typed = ["schedule", "from"];
    static readonly Regex Variable = new(@"\$\{(\w+)\}");
    const string NoValues = "\n\n(Started by hand, not by an event: the ${...} variables above have no value. Do your best without them.)";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Fields { get; init; }

    // A command line run instead of the agent; the event's values reach it as environment variables (Environment).
    public string? Command { get; init; }

    // The agent's model: a level of Settings.Levels (the Settings page maps it per CLI) or a model name; null: the CLI's default.
    public string? Model { get; init; }

    [JsonIgnore] public TriggerKind Kind => TriggerKind.Find(On)!; // checked by Load

    // A text field of its kind, by name (any case); null when absent.
    public string? Field(string name) =>
        Fields?.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v
            ? v.GetString() : null;

    // Fills ${name} (any case) from the values. A prompt of an event kind using no variable gets the event appended instead.
    // Run by hand there are no values: the variables stay as is, with a note saying so.
    public string PromptWith(IReadOnlyDictionary<string, string> values)
    {
        var text = Prompt!; // a prompt trigger
        if (Kind.Context is not { } context) return text;
        if (values.Count == 0) return Variable.IsMatch(text) ? text + NoValues : text;
        var prompt = Variable.IsMatch(text) ? text : text + "\n\n" + context;
        return Variable.Replace(prompt, m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v : m.Value);
    }

    // ${subject} -> BUTLER_SUBJECT: never pasted into a command line, where a mail subject could add commands.
    public static string EnvironmentName(string variable) => "BUTLER_" + variable.ToUpperInvariant();

    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> values) =>
        values.ToDictionary(v => EnvironmentName(v.Key), v => v.Value);

    public static List<Trigger> Load(string path)
    {
        var triggers = JsonSerializer.Deserialize<List<Trigger>>(File.ReadAllText(path), Json) ?? [];
        Check(triggers);
        return triggers;
    }

    // Writes the triggers file in its usual layout (one key per line, lists on one line), keeping its line endings.
    // Throws a FormatException, and writes nothing, when a trigger is wrong.
    public static void Save(string path, IReadOnlyList<Trigger> triggers)
    {
        Check(triggers);
        var nl = File.Exists(path) && File.ReadAllText(path).Contains("\r\n") ? "\r\n" : "\n";
        var text = "[" + nl + string.Join("," + nl, triggers.Select(t => t.Write(nl))) + nl + "]" + nl;
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // A trigger from the dashboard's editor: the values of its kind's form, by field name (a list field has one value per line).
    // The text is its prompt, or its command line.
    public static Trigger FromForm(string name, string on, string text, IReadOnlyDictionary<string, string> values, bool command = false, string? model = null)
    {
        var kind = TriggerKind.Find(on) ?? throw new FormatException($"Unknown kind '{on}'");
        string? Text(string field) => values.TryGetValue(field, out var v) && v.Trim() is { Length: > 0 } t ? t : null;
        string[]? List(string field) => Text(field)?.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var fields = kind.Form.Where(f => !Typed.Contains(f.Name) && Text(f.Name) != null).ToDictionary(f => f.Name,
            f => f.Type == "list" ? JsonSerializer.SerializeToElement(List(f.Name)) : JsonSerializer.SerializeToElement(Text(f.Name)));
        return new Trigger(name.Trim(), on, command ? null : text.Trim(), on == "schedule" ? Text("schedule") : null, on == "mail" ? List("from") : null)
        {
            Command = command ? text.Trim() : null,
            Model = command || string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Fields = fields.Count > 0 ? fields : null,
        };
    }

    static void Check(IEnumerable<Trigger> triggers)
    {
        foreach (var t in triggers) t.Validate();
        var dup = triggers.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new FormatException($"Trigger '{dup.Key}' is declared twice");
    }

    string Write(string nl)
    {
        string Value(object v) => v is string[] list ? "[" + string.Join(", ", list.Select(i => JsonSerializer.Serialize(i, WriteJson))) + "]"
            : v is JsonElement { ValueKind: JsonValueKind.Array } a ? Value(a.EnumerateArray().Select(i => i.GetString()!).ToArray())
            : JsonSerializer.Serialize(v is JsonElement e ? e.GetString() : v, WriteJson);
        var lines = new List<string> { $"\"name\": {Value(Name)}", $"\"on\": {Value(On)}" };
        foreach (var f in Kind.Form)
        {
            object? v = f.Name == "schedule" ? Schedule : f.Name == "from" ? From
                : Fields?.FirstOrDefault(x => x.Key.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } x ? x.Value : null;
            if (v != null) lines.Add($"{JsonSerializer.Serialize(f.Name)}: {Value(v)}");
        }
        if (Model != null) lines.Add($"\"model\": {Value(Model)}");
        lines.Add(Command != null ? $"\"command\": {Value(Command)}" : $"\"prompt\": {Value(Prompt!)}");
        return "  {" + nl + string.Join("," + nl, lines.Select(l => "    " + l)) + nl + "  }";
    }

    void Validate()
    {
        if (string.IsNullOrEmpty(Name) || !Regex.IsMatch(Name, "^[A-Za-z0-9_-]+$"))
            throw new FormatException($"Trigger name '{Name}' must only use letters, digits, '-' and '_'");
        if (Command != null && Prompt != null) throw new FormatException($"Trigger '{Name}': has both a prompt and a command, keep one");
        if (Command == null && string.IsNullOrWhiteSpace(Prompt)) throw new FormatException($"Trigger '{Name}': prompt is empty (or give a command instead)");
        if (Command != null && string.IsNullOrWhiteSpace(Command)) throw new FormatException($"Trigger '{Name}': command is empty");
        if (Command != null && Model != null) throw new FormatException($"Trigger '{Name}': a command runs no agent, it has no model");
        var kind = TriggerKind.Find(On)
            ?? throw new FormatException($"Trigger '{Name}': on must be {string.Join(", ", TriggerKind.All.Select(k => $"'{k.Name}'"))}, not '{On}'");
        kind.Validate(this);
        foreach (var field in Fields?.Keys ?? Enumerable.Empty<string>())
            if (!kind.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                throw new FormatException($"Trigger '{Name}': unknown field '{field}' for an '{On}' trigger");
        if (Command != null && Variable.Match(Command) is { Success: true } v)
            throw new FormatException($"Trigger '{Name}': a command gets the event as environment variables, not {v.Value}" + (kind.Variables.Length > 0
                ? $" (use {string.Join(", ", kind.Variables.Select(EnvironmentName))})"
                : $" (an '{On}' trigger has none)"));
        foreach (Match m in Variable.Matches(Prompt ?? ""))
            if (!kind.Variables.Contains(m.Groups[1].Value.ToLowerInvariant()))
                throw new FormatException($"Trigger '{Name}': unknown variable {m.Value}" + (kind.Variables.Length > 0
                    ? $" (use {string.Join(", ", kind.Variables.Select(v => "${" + v + "}"))})"
                    : $" (an '{On}' trigger has no variables)"));
    }
}
