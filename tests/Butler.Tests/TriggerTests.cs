namespace Butler.Tests;

public class TriggerTests
{
    static List<Trigger> LoadJson(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try { return Trigger.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Loads_schedule_and_mail_triggers()
    {
        var triggers = LoadJson("""
            [
              { "name": "timesheet", "on": "schedule", "schedule": "monthly:28 09:00", "prompt": "/timesheet" },
              { "name": "triage-mail", "on": "mail", "from": ["a@example.com"], "prompt": "/triage-mail" }
            ]
            """);
        Assert.Equal(["timesheet", "triage-mail"], triggers.Select(t => t.Name));
        Assert.Equal(["a@example.com"], triggers[1].From!);
    }

    [Theory]
    [InlineData("""[{ "name": "x", "on": "schedule", "schedule": "every day", "prompt": "p" }]""", "Bad schedule")]
    [InlineData("""[{ "name": "x", "on": "schedule", "prompt": "p" }]""", "schedule is missing")]
    [InlineData("""[{ "name": "x", "on": "mail", "from": [], "prompt": "p" }]""", "from is empty")]
    [InlineData("""[{ "name": "x", "on": "webhook", "prompt": "p" }]""", "on must be")]
    [InlineData("""[{ "name": "a b", "on": "mail", "from": ["a"], "prompt": "p" }]""", "must only use")]
    [InlineData("""[{ "name": "x", "on": "mail", "from": ["a"], "prompt": "p" }, { "name": "X", "on": "mail", "from": ["a"], "prompt": "p" }]""", "declared twice")]
    [InlineData("""[{ "name": "x", "on": "mail", "from": ["a"], "prompt": "/x ${body}" }]""", "unknown variable ${body}")]
    [InlineData("""[{ "name": "x", "on": "schedule", "schedule": "daily 09:00", "prompt": "/x ${from}" }]""", "unknown variable ${from}")]
    public void Rejects_invalid_triggers(string json, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => LoadJson(json)).Message);

    static readonly Dictionary<string, string> Mail = new()
    {
        ["from"] = "a@example.com", ["subject"] = "Action Required | CVE-2026-1", ["received"] = "2026-10-29 13:40",
    };

    [Fact]
    public void Mail_variables_are_replaced_in_the_prompt()
    {
        var t = LoadJson("""[{ "name": "x", "on": "mail", "from": ["a"], "prompt": "/triage-mail only the mail from ${FROM}: \"${subject}\" (${received})" }]""")[0];
        Assert.Equal("/triage-mail only the mail from a@example.com: \"Action Required | CVE-2026-1\" (2026-10-29 13:40)", t.PromptWith(Mail));
    }

    [Fact]
    public void Mail_prompt_run_by_hand_keeps_the_variables_and_says_why()
    {
        var t = new Trigger("x", "mail", "/triage-mail mail from ${from}", From: ["a"]);
        var prompt = t.PromptWith(new Dictionary<string, string>());
        Assert.StartsWith("/triage-mail mail from ${from}\n\n(Started by hand, not by a mail", prompt);
        Assert.Equal("/triage-mail", new Trigger("x", "mail", "/triage-mail", From: ["a"]).PromptWith(new Dictionary<string, string>()));
    }

    [Fact]
    public void Mail_prompt_without_variables_gets_the_mail_appended()
    {
        var t = new Trigger("x", "mail", "/triage-mail", From: ["a"]);
        Assert.Equal("/triage-mail\n\nTriggered by this mail: from a@example.com, subject \"Action Required | CVE-2026-1\", received 2026-10-29 13:40.", t.PromptWith(Mail));
    }

    [Fact]
    public void Parses_schedules()
    {
        Assert.Equal(new Schedule(ScheduleKind.Daily, new TimeSpan(9, 0, 0)), Schedule.Parse("daily 09:00"));
        Assert.Equal(new Schedule(ScheduleKind.Weekly, new TimeSpan(7, 30, 0), DayOfWeek: DayOfWeek.Monday), Schedule.Parse("weekly:mon 7:30"));
        Assert.Equal(new Schedule(ScheduleKind.Monthly, new TimeSpan(9, 0, 0), DayOfMonth: 28), Schedule.Parse("monthly:28 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("monthly:32 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("weekly:xyz 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("daily 25:00"));
    }
}
