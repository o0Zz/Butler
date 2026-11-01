using System.Text.RegularExpressions;

namespace Butler;

// "schedule": polled by the tray like the other kinds. A time missed while Butler was closed or the PC off or asleep
// runs at the next poll, once however many were missed. Checkpoint: <state>/schedule.json, the last time run per trigger.
public sealed class ScheduleTrigger : TriggerKind
{
    public override string Name => "schedule";
    public override string Label => T("Schedule");
    public override FormField[] Form => [new("schedule", T("When"), "schedule")];

    public override void Validate(Trigger t) =>
        Schedule.Parse(t.Schedule ?? throw new FormatException($"Trigger '{t.Name}': schedule is missing"));

    public override string Describe(Trigger t)
    {
        var s = Schedule.Parse(t.Schedule!);
        var at = s.At.ToString(@"hh\:mm");
        return s.Kind switch
        {
            ScheduleKind.Daily => T("Every day at {0}", at),
            ScheduleKind.Weekly when s.Days == Schedule.Weekdays => T("Weekdays at {0}", at),
            ScheduleKind.Weekly => T("Every {0} at {1}", string.Join(", ", Enum.GetValues<DayOfWeek>().Where(d => s.Days.HasFlag(Schedule.Flag(d)))
                .Select(Lang.Culture.DateTimeFormat.GetDayName)), at),
            _ => T("Monthly on day {0} at {1}", s.DayOfMonth, at),
        };
    }

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir, Platform platform) =>
        Poll(triggers, stateDir, DateTime.Now);

    internal List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, string stateDir, DateTime now)
    {
        var path = Path.Combine(stateDir, "schedule.json");
        var old = Checkpoint.Load<Dictionary<string, DateTime>>(path);
        var seen = new Dictionary<string, DateTime>(); // only the current triggers: removed ones are forgotten
        var runs = new List<(Trigger, Dictionary<string, string>)>();
        foreach (var t in triggers)
        {
            var key = $"{t.Name}|{t.Schedule}"; // a new trigger or schedule starts from now: a time already gone does not run
            if (!old.TryGetValue(key, out var done))
            {
                seen[key] = now;
                continue;
            }
            var last = Schedule.Parse(t.Schedule!).Last(now);
            if (last > done) runs.Add((t, []));
            seen[key] = last > done ? last : done;
        }
        Checkpoint.Save(path, seen);
        return runs;
    }
}

[Flags]
public enum DaysOfTheWeek { Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64 }

public enum ScheduleKind { Daily, Weekly, Monthly }

// "daily 09:00"; days of the week: "fri 09:00", "mon-fri 09:00", "mon,wed,fri 09:00" (ranges and lists can mix);
// "monthly:28 09:00". No cron.
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

    // The latest time at or before now, and the first one after now.
    public DateTime Last(DateTime now) => Times(now.Date, -1).First(t => t <= now);
    public DateTime Next(DateTime now) => Times(now.Date, 1).First(t => t > now);

    // Its times day by day from a day, forward or back; 400 days always hold one (a day 31 is skipped by shorter months).
    IEnumerable<DateTime> Times(DateTime from, int step) =>
        Enumerable.Range(0, 400).Select(i => from.AddDays(i * step)).Where(On).Select(day => day + At);

    bool On(DateTime day) => Kind switch
    {
        ScheduleKind.Daily => true,
        ScheduleKind.Weekly => Days.HasFlag(Flag(day.DayOfWeek)),
        _ => day.Day == DayOfMonth,
    };
}
