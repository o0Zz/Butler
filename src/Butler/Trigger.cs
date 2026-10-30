using System.Text.Json;
using System.Text.RegularExpressions;

namespace Butler;

// One entry of butler.json. "on" is "schedule" (Schedule required) or "mail" (From required).
public sealed record Trigger(string Name, string On, string Prompt, string? Schedule = null, string[]? From = null)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly Regex Variable = new(@"\$\{(\w+)\}");
    public static readonly string[] MailVariables = ["from", "subject", "received"];
    const string MailContext = "\n\nTriggered by this mail: from ${from}, subject \"${subject}\", received ${received}.";
    const string NoMail = "\n\n(Started by hand, not by a mail: the ${...} variables above have no value. Do your best without them.)";

    // Fills ${name} (any case) from the values. A mail prompt with no variable gets the mail appended instead.
    // A mail trigger run by hand has no values: the variables stay as is, with a note saying so.
    public string PromptWith(IReadOnlyDictionary<string, string> values)
    {
        if (On == "mail" && values.Count == 0) return Variable.IsMatch(Prompt) ? Prompt + NoMail : Prompt;
        var prompt = On == "mail" && !Variable.IsMatch(Prompt) ? Prompt + MailContext : Prompt;
        return Variable.Replace(prompt, m => values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v : m.Value);
    }

    public static List<Trigger> Load(string path)
    {
        var triggers = JsonSerializer.Deserialize<List<Trigger>>(File.ReadAllText(path), Json) ?? [];
        foreach (var t in triggers) t.Validate();
        var dup = triggers.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new FormatException($"Trigger '{dup.Key}' is declared twice");
        return triggers;
    }

    void Validate()
    {
        if (string.IsNullOrEmpty(Name) || !Regex.IsMatch(Name, "^[A-Za-z0-9_-]+$"))
            throw new FormatException($"Trigger name '{Name}' must only use letters, digits, '-' and '_'");
        if (string.IsNullOrWhiteSpace(Prompt)) throw new FormatException($"Trigger '{Name}': prompt is empty");
        switch (On)
        {
            case "schedule": Butler.Schedule.Parse(Schedule ?? throw new FormatException($"Trigger '{Name}': schedule is missing")); break;
            case "mail": if (From is not { Length: > 0 }) throw new FormatException($"Trigger '{Name}': from is empty"); break;
            default: throw new FormatException($"Trigger '{Name}': on must be 'schedule' or 'mail', not '{On}'");
        }
        var known = On == "mail" ? MailVariables : [];
        foreach (Match m in Variable.Matches(Prompt))
            if (!known.Contains(m.Groups[1].Value.ToLowerInvariant()))
                throw new FormatException($"Trigger '{Name}': unknown variable {m.Value}" +
                    (known.Length > 0 ? $" (use {string.Join(", ", known.Select(v => "${" + v + "}"))})" : " (only mail triggers have variables)"));
    }
}

public enum ScheduleKind { Daily, Weekly, Monthly }

// "daily 09:00", "weekly:mon 09:00", "monthly:28 09:00"
public sealed record Schedule(ScheduleKind Kind, TimeSpan At, int DayOfMonth = 0, DayOfWeek DayOfWeek = default)
{
    static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static Schedule Parse(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(daily|weekly:(\w{3})|monthly:(\d{1,2})) (\d{1,2}):(\d{2})$");
        if (!m.Success) throw new FormatException($"Bad schedule '{text}' (expected 'daily 09:00', 'weekly:mon 09:00' or 'monthly:28 09:00')");
        var at = new TimeSpan(int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0);
        if (at.TotalHours >= 24) throw new FormatException($"Bad time in schedule '{text}'");

        if (m.Groups[2].Success)
        {
            var day = Array.IndexOf(Days, m.Groups[2].Value.ToLowerInvariant());
            if (day < 0) throw new FormatException($"Bad day in schedule '{text}' (use {string.Join(", ", Days)})");
            return new(ScheduleKind.Weekly, at, DayOfWeek: (DayOfWeek)day);
        }
        if (m.Groups[3].Success)
        {
            var day = int.Parse(m.Groups[3].Value);
            if (day is < 1 or > 31) throw new FormatException($"Bad day of month in schedule '{text}'");
            return new(ScheduleKind.Monthly, at, DayOfMonth: day);
        }
        return new(ScheduleKind.Daily, at);
    }
}
