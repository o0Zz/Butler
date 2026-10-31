using System.Text.RegularExpressions;
using DaysOfTheWeek = Microsoft.Win32.TaskScheduler.DaysOfTheWeek;

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
            ScheduleKind.Weekly when s.Days == Schedule.Weekdays => $"Weekdays at {at}",
            ScheduleKind.Weekly => $"Every {string.Join(", ", Enum.GetValues<DayOfWeek>().Where(d => s.Days.HasFlag(Schedule.Flag(d))))} at {at}",
            _ => $"Monthly on day {s.DayOfMonth} at {at}",
        };
    }
}

public enum ScheduleKind { Daily, Weekly, Monthly }

// "daily 09:00"; days of the week: "fri 09:00", "mon-fri 09:00", "mon,wed,fri 09:00" (ranges and lists can mix);
// "monthly:28 09:00". What the Task Scheduler can do (daily, weekly on days, monthly on a day), so no cron.
public sealed record Schedule(ScheduleKind Kind, TimeSpan At, int DayOfMonth = 0, DaysOfTheWeek Days = 0)
{
    static readonly string[] Names = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    public const DaysOfTheWeek Weekdays = DaysOfTheWeek.Monday | DaysOfTheWeek.Tuesday | DaysOfTheWeek.Wednesday | DaysOfTheWeek.Thursday | DaysOfTheWeek.Friday;

    public static DaysOfTheWeek Flag(DayOfWeek day) => (DaysOfTheWeek)(1 << (int)day);

    public static Schedule Parse(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(daily|monthly:(\d{1,2})|([a-z,-]+)) (\d{1,2}):(\d{2})$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException($"Bad schedule '{text}' (expected 'daily 09:00', 'mon-fri 09:00', 'mon,wed 09:00' or 'monthly:28 09:00')");
        var at = new TimeSpan(int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0);
        if (at.TotalHours >= 24) throw new FormatException($"Bad time in schedule '{text}'");

        if (m.Groups[3].Success)
        {
            DaysOfTheWeek days = 0;
            foreach (var part in m.Groups[3].Value.ToLowerInvariant().Split(','))
            {
                var ends = part.Split('-').Select(n => Array.IndexOf(Names, n)).ToArray();
                if (ends.Length > 2 || ends.Any(d => d < 0))
                    throw new FormatException($"Bad days '{part}' in schedule '{text}' (use {string.Join(", ", Names)}, a range like mon-fri, or a list like mon,wed)");
                for (var d = ends[0]; ; d = (d + 1) % 7) // a range can wrap: fri-mon
                {
                    days |= Flag((DayOfWeek)d);
                    if (d == ends[^1]) break;
                }
            }
            return new(ScheduleKind.Weekly, at, Days: days);
        }
        if (m.Groups[2].Success)
        {
            var day = int.Parse(m.Groups[2].Value);
            if (day is < 1 or > 31) throw new FormatException($"Bad day of month in schedule '{text}'");
            return new(ScheduleKind.Monthly, at, DayOfMonth: day);
        }
        return new(ScheduleKind.Daily, at);
    }
}
