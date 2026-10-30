using System.Text.RegularExpressions;

namespace Butler;

// "schedule": a Windows scheduled task in \Butler\ (TaskSync) runs it, even when the tray is closed.
public sealed class ScheduleTrigger : TriggerKind
{
    public override string Name => "schedule";
    public override string Label => "Schedule";
    public override FormField[] Form => [new("schedule", "When", "schedule")];
    public override TimeSpan? Interval => null;

    public override void Validate(Trigger t) =>
        Schedule.Parse(t.Schedule ?? throw new FormatException($"Trigger '{t.Name}': schedule is missing"));

    public override string Describe(Trigger t)
    {
        var s = Schedule.Parse(t.Schedule!);
        var at = s.At.ToString(@"hh\:mm");
        return s.Kind switch
        {
            ScheduleKind.Daily => $"Every day at {at}",
            ScheduleKind.Weekly => $"Every {s.DayOfWeek} at {at}",
            _ => $"Monthly on day {s.DayOfMonth} at {at}",
        };
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
