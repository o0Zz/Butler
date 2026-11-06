
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
    public void A_prompt_trigger_keeps_its_model_and_a_command_has_none()
    {
        var path = Path.GetTempFileName();
        try
        {
            Trigger.Save(path, [Trigger.FromForm("brief", "schedule", "/morning-brief", new Dictionary<string, string> { ["schedule"] = "daily 08:30" }, model: "powerful")]);
            Assert.Contains("\"model\": \"powerful\",\n    \"prompt\"", File.ReadAllText(path).Replace("\r\n", "\n")); // just before the prompt
            Assert.Equal("powerful", Trigger.Load(path).Single().Model);
            Assert.Null(Trigger.FromForm("pull", "schedule", "git pull", new Dictionary<string, string> { ["schedule"] = "daily 08:30" }, command: true, model: "fast").Model);
        }
        finally { File.Delete(path); }
        Assert.Contains("a command runs no agent, it has no model", Assert.Throws<FormatException>(() => LoadJson(
            """[{ "name": "pull", "on": "schedule", "schedule": "daily 08:30", "model": "fast", "command": "git pull" }]""")).Message);
    }

    [Fact]
    public void Creates_an_empty_file_with_its_folder_when_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(dir, "skills", "butler.json");
        try
        {
            Assert.Empty(Trigger.Load(path));
            Assert.Equal("[]" + Environment.NewLine, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
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
    [InlineData("""[{ "name": "x", "on": "schedule", "schedule": "daily 09:00", "prompt": "/x", "command": "git pull" }]""", "both a prompt and a command")]
    [InlineData("""[{ "name": "x", "on": "schedule", "schedule": "daily 09:00", "command": " " }]""", "command is empty")]
    [InlineData("""[{ "name": "x", "on": "schedule", "schedule": "daily 09:00" }]""", "prompt is empty")]
    [InlineData("""[{ "name": "x", "on": "mail", "from": ["a"], "command": "log.cmd ${subject}" }]""", "environment variables, not ${subject} (use BUTLER_FROM, BUTLER_SUBJECT, BUTLER_RECEIVED)")]
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
        Assert.StartsWith("/triage-mail mail from ${from}\n\n(Started by hand, not by an event", prompt);
        Assert.Equal("/triage-mail", new Trigger("x", "mail", "/triage-mail", From: ["a"]).PromptWith(new Dictionary<string, string>()));
    }

    [Fact]
    public void Mail_prompt_without_variables_gets_the_mail_appended()
    {
        var t = new Trigger("x", "mail", "/triage-mail", From: ["a"]);
        Assert.Equal("/triage-mail\n\nTriggered by this mail: from a@example.com, subject \"Action Required | CVE-2026-1\", received 2026-10-29 13:40.", t.PromptWith(Mail));
    }

    [Fact]
    public void Schedules_give_their_last_and_next_times()
    {
        var friday = DateTime.Parse("2026-10-30 12:00");
        Assert.Equal(DateTime.Parse("2026-10-30 07:30"), Schedule.Parse("mon-fri 07:30").Last(friday));
        Assert.Equal(DateTime.Parse("2026-11-02 07:30"), Schedule.Parse("mon-fri 07:30").Next(friday));
        Assert.Equal(friday, Schedule.Parse("daily 12:00").Last(friday)); // at the minute
        Assert.Equal(DateTime.Parse("2026-10-31 12:00"), Schedule.Parse("daily 12:00").Next(friday));
        Assert.Equal(DateTime.Parse("2026-12-31 09:00"), Schedule.Parse("monthly:31 09:00").Next(DateTime.Parse("2026-11-01 00:00"))); // no 31 November
        Assert.Equal(DateTime.Parse("2026-10-31 09:00"), Schedule.Parse("monthly:31 09:00").Last(DateTime.Parse("2026-11-30 00:00")));
    }

    [Fact]
    public void Parses_schedules()
    {
        Assert.Equal(new Schedule(ScheduleKind.Daily, new TimeSpan(9, 0, 0)), Schedule.Parse("daily 09:00"));
        Assert.Equal(new Schedule(ScheduleKind.Weekly, new TimeSpan(7, 30, 0), Days: DaysOfTheWeek.Monday), Schedule.Parse("mon 7:30"));
        Assert.Equal(new Schedule(ScheduleKind.Weekly, new TimeSpan(8, 30, 0), Days: Schedule.Weekdays), Schedule.Parse("mon-fri 08:30"));
        Assert.Equal(DaysOfTheWeek.Monday | DaysOfTheWeek.Wednesday | DaysOfTheWeek.Friday, Schedule.Parse("Mon,wed,FRI 09:00").Days);
        Assert.Equal(DaysOfTheWeek.Friday | DaysOfTheWeek.Saturday | DaysOfTheWeek.Sunday | DaysOfTheWeek.Monday, Schedule.Parse("fri-mon 09:00").Days); // wraps
        Assert.Equal(DaysOfTheWeek.Monday | DaysOfTheWeek.Tuesday | DaysOfTheWeek.Thursday, Schedule.Parse("mon-tue,thu 09:00").Days);
        Assert.Equal(new Schedule(ScheduleKind.Monthly, new TimeSpan(9, 0, 0), DayOfMonth: 28), Schedule.Parse("monthly:28 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("monthly:32 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("xyz 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("mon-tue-wed 09:00"));
        Assert.Throws<FormatException>(() => Schedule.Parse("weekly:mon 09:00")); // the old format
        Assert.Throws<FormatException>(() => Schedule.Parse("30 8 * * 1-5")); // no cron
        Assert.Throws<FormatException>(() => Schedule.Parse("daily 25:00"));
    }

    [Theory]
    [InlineData("daily 08:30", "Every day at 08:30")]
    [InlineData("mon-fri 08:30", "Weekdays at 08:30")]
    [InlineData("mon,fri 12:00", "Every Monday, Friday at 12:00")]
    [InlineData("monthly:28 09:00", "Monthly on day 28 at 09:00")]
    public void Describes_schedules(string schedule, string text) =>
        Assert.Equal(text, new ScheduleTrigger().Describe(new Trigger("x", "schedule", "/x", Schedule: schedule)));

    // What the dashboard's trigger editor does: build from the form values, then write the whole file.
    [Fact]
    public void Edited_triggers_are_written_in_the_file_layout_and_read_back()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[\r\n]\r\n"); // CRLF, kept
            List<Trigger> triggers =
            [
                Trigger.FromForm("brief", "schedule", "/morning-brief", new Dictionary<string, string> { ["schedule"] = "daily 08:30" }),
                Trigger.FromForm("vuln", "mail", "/triage-mail \"${subject}\" é", new Dictionary<string, string> { ["from"] = " a@example.com \n\nb@example.com" }),
                Trigger.FromForm("specs", "file", "/fmea ${path}", new Dictionary<string, string> { ["folder"] = @"C:\in", ["filter"] = "" }),
            ];
            Trigger.Save(path, triggers);

            Assert.Equal(string.Join("\r\n",
                "[",
                "  {",
                "    \"name\": \"brief\",",
                "    \"on\": \"schedule\",",
                "    \"schedule\": \"daily 08:30\",",
                "    \"prompt\": \"/morning-brief\"",
                "  },",
                "  {",
                "    \"name\": \"vuln\",",
                "    \"on\": \"mail\",",
                "    \"from\": [\"a@example.com\", \"b@example.com\"],",
                "    \"prompt\": \"/triage-mail \\\"${subject}\\\" é\"",
                "  },",
                "  {",
                "    \"name\": \"specs\",",
                "    \"on\": \"file\",",
                "    \"folder\": \"C:\\\\in\",",
                "    \"prompt\": \"/fmea ${path}\"",
                "  }",
                "]",
                ""), File.ReadAllText(path));

            var back = Trigger.Load(path);
            Assert.Equal(["brief", "vuln", "specs"], back.Select(t => t.Name));
            Assert.Equal(["a@example.com", "b@example.com"], back[1].From!);
            Assert.Equal(@"C:\in", back[2].Field("folder"));
            Assert.Null(back[2].Field("filter"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Command_trigger_is_read_written_and_gets_the_event_as_environment_variables()
    {
        var path = Path.GetTempFileName();
        try
        {
            Trigger.Save(path, [Trigger.FromForm("pull", "file", " git pull ", new Dictionary<string, string> { ["folder"] = @"C:\in" }, command: true)]);
            Assert.Contains("\"command\": \"git pull\"", File.ReadAllText(path));
            Assert.DoesNotContain("prompt", File.ReadAllText(path));

            var t = Trigger.Load(path).Single();
            Assert.Equal("git pull", t.Command);
            Assert.Null(t.Prompt);
            Assert.Equal(new Dictionary<string, string> { ["BUTLER_PATH"] = @"C:\in\a.txt", ["BUTLER_NAME"] = "a.txt" },
                Trigger.Environment(new Dictionary<string, string> { ["path"] = @"C:\in\a.txt", ["name"] = "a.txt" }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_invalid_edit_writes_nothing()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string before = """[{ "name": "a", "on": "schedule", "schedule": "daily 09:00", "prompt": "/a" }]""";
            File.WriteAllText(path, before);
            var list = Trigger.Load(path);
            list.Add(Trigger.FromForm("A", "schedule", "/b", new Dictionary<string, string> { ["schedule"] = "daily 10:00" }));
            Assert.Contains("declared twice", Assert.Throws<FormatException>(() => Trigger.Save(path, list)).Message);
            Assert.Throws<FormatException>(() => Trigger.Save(path, [Trigger.FromForm("x", "mail", "/x", new Dictionary<string, string>())]));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
