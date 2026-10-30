using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.TaskScheduler;

namespace Butler;

// Makes the Task Scheduler folder \Butler\ match the schedule triggers of butler.json.
// Never touches tasks outside that folder.
public static class TaskSync
{
    public const string DefaultFolder = "Butler";
    const string Marker = "butler:";

    public sealed record Result(List<string> Created, List<string> Updated, List<string> Deleted);

    public static Result Sync(IEnumerable<Trigger> triggers, string exePath, string folderName = DefaultFolder)
    {
        var wanted = triggers.Where(t => t.On == "schedule").ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var result = new Result([], [], []);

        using var ts = new TaskService();
        var folder = ts.RootFolder.SubFolders.Exists(folderName) ? ts.GetFolder(folderName) : ts.RootFolder.CreateFolder(folderName);

        foreach (var task in folder.Tasks.ToList())
        {
            if (!wanted.ContainsKey(task.Name))
            {
                folder.DeleteTask(task.Name);
                result.Deleted.Add(task.Name);
            }
        }

        foreach (var t in wanted.Values)
        {
            var hash = Marker + Hash(t.Schedule!, exePath);
            var existing = folder.Tasks.FirstOrDefault(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            if (existing?.Definition.RegistrationInfo.Description == hash) continue;

            var td = ts.NewTask();
            td.RegistrationInfo.Description = hash;
            td.Principal.LogonType = TaskLogonType.InteractiveToken; // needs the user session: notifications, Chrome
            td.Settings.StartWhenAvailable = true;                    // catch up a run missed while logged off
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.FromHours(2);
            td.Triggers.Add(ToTaskTrigger(Butler.Schedule.Parse(t.Schedule!)));
            td.Actions.Add(new ExecAction(exePath, $"run {t.Name}"));
            folder.RegisterTaskDefinition(t.Name, td);
            (existing == null ? result.Created : result.Updated).Add(t.Name);
        }
        return result;
    }

    public static Dictionary<string, DateTime> NextRuns(string folderName = DefaultFolder)
    {
        using var ts = new TaskService();
        if (!ts.RootFolder.SubFolders.Exists(folderName)) return [];
        return ts.GetFolder(folderName).Tasks.ToDictionary(t => t.Name, t => t.NextRunTime, StringComparer.OrdinalIgnoreCase);
    }

    static Microsoft.Win32.TaskScheduler.Trigger ToTaskTrigger(Schedule s)
    {
        var start = DateTime.Today.Add(s.At);
        Microsoft.Win32.TaskScheduler.Trigger trigger = s.Kind switch
        {
            ScheduleKind.Daily => new DailyTrigger(),
            ScheduleKind.Weekly => new WeeklyTrigger((DaysOfTheWeek)(1 << (int)s.DayOfWeek)),
            _ => new MonthlyTrigger(s.DayOfMonth),
        };
        trigger.StartBoundary = start;
        return trigger;
    }

    static string Hash(string schedule, string exePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{schedule}|{exePath}")))[..16];
}
