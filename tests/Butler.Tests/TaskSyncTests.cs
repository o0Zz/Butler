using Microsoft.Win32.TaskScheduler;

namespace Butler.Tests;

// Runs against the real Task Scheduler, in a throwaway folder (never \Butler\).
public class TaskSyncTests : IDisposable
{
    readonly string folder = "ButlerTest-" + Guid.NewGuid().ToString()[..8];
    const string Exe = @"C:\fake\Butler.exe";

    static Trigger Sched(string name, string schedule) => new(name, "schedule", "/x", Schedule: schedule);

    static Microsoft.Win32.TaskScheduler.Task? Find(TaskFolder f, string name) => f.Tasks.FirstOrDefault(t => t.Name == name);

    [Fact]
    public void Creates_updates_and_deletes_tasks_to_match_triggers()
    {
        var r = TaskSync.Sync([Sched("timesheet", "monthly:28 09:00"), Sched("daily-one", "daily 08:15"),
                               new Trigger("mail-one", "mail", "/x", From: ["a@b.c"])], Exe, folder);
        Assert.Equal(["timesheet", "daily-one"], r.Created);

        using (var ts = new TaskService())
        {
            var f = ts.GetFolder(folder);
            Assert.Equal(["daily-one", "timesheet"], f.Tasks.Select(t => t.Name).Order());
            var timesheet = Find(f, "timesheet")!.Definition;
            var monthly = Assert.IsType<MonthlyTrigger>(Assert.Single(timesheet.Triggers));
            Assert.Equal([28], monthly.DaysOfMonth);
            Assert.Equal(new TimeSpan(9, 0, 0), monthly.StartBoundary.TimeOfDay);
            var action = Assert.IsType<ExecAction>(Assert.Single(timesheet.Actions));
            Assert.Equal(Exe, action.Path);
            Assert.Equal("run timesheet", action.Arguments);
            Assert.Equal(TaskLogonType.InteractiveToken, timesheet.Principal.LogonType);
            Assert.True(timesheet.Settings.StartWhenAvailable);
        }

        // Unchanged triggers are left alone.
        r = TaskSync.Sync([Sched("timesheet", "monthly:28 09:00"), Sched("daily-one", "daily 08:15")], Exe, folder);
        Assert.Empty(r.Created.Concat(r.Updated).Concat(r.Deleted));

        // Schedule changed on one, the other removed from triggers.json.
        r = TaskSync.Sync([Sched("timesheet", "fri 17:00")], Exe, folder);
        Assert.Equal(["timesheet"], r.Updated);
        Assert.Equal(["daily-one"], r.Deleted);

        using (var ts = new TaskService())
        {
            var f = ts.GetFolder(folder);
            var weekly = Assert.IsType<WeeklyTrigger>(Assert.Single(Assert.Single(f.Tasks).Definition.Triggers));
            Assert.Equal(DaysOfTheWeek.Friday, weekly.DaysOfWeek);
            Assert.Contains("timesheet", TaskSync.NextRuns(folder).Keys);
        }

        // Weekdays only: one weekly task on Monday to Friday, never on a weekend.
        TaskSync.Sync([Sched("timesheet", "mon-fri 08:30")], Exe, folder);
        using (var ts = new TaskService())
        {
            var task = Assert.Single(ts.GetFolder(folder).Tasks);
            var weekly = Assert.IsType<WeeklyTrigger>(Assert.Single(task.Definition.Triggers));
            Assert.Equal(DaysOfTheWeek.Monday | DaysOfTheWeek.Tuesday | DaysOfTheWeek.Wednesday | DaysOfTheWeek.Thursday | DaysOfTheWeek.Friday, weekly.DaysOfWeek);
            Assert.Equal(1, weekly.WeeksInterval);
            Assert.True(task.NextRunTime.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
            Assert.Equal(new TimeSpan(8, 30, 0), task.NextRunTime.TimeOfDay);
        }
    }

    public void Dispose()
    {
        using var ts = new TaskService();
        if (!ts.RootFolder.SubFolders.Exists(folder)) return;
        var f = ts.GetFolder(folder);
        foreach (var t in f.Tasks.ToList()) f.DeleteTask(t.Name);
        ts.RootFolder.DeleteFolder(folder);
    }
}
