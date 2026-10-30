namespace Butler.Tests;

public class CheckpointTests
{
    static readonly DateTime T0 = new(2026, 10, 29, 8, 0, 0);

    static Mail M(string id, int minutes, string from = "x@y.z") => new(id, T0.AddMinutes(minutes), from, "subject " + id);

    static List<Mail> Take(Checkpoint cp, IEnumerable<Mail> mails) => cp.TakeNew(mails, m => m.EntryId, m => m.Received);

    [Fact]
    public void Returns_only_new_mails_oldest_first_and_never_twice()
    {
        var cp = new Checkpoint { Last = T0 };

        Assert.Equal(["a", "b"], Take(cp, [M("b", 2), M("a", 1), M("old", -5)]).Select(m => m.EntryId));
        Assert.Equal(T0.AddMinutes(2), cp.Last);

        // The next poll sees "b" again (received at the checkpoint) plus a mail with the same timestamp.
        Assert.Equal(["c"], Take(cp, [M("c", 2), M("b", 2), M("a", 1)]).Select(m => m.EntryId));
        Assert.Empty(Take(cp, [M("c", 2), M("b", 2)]));
        Assert.Equal(["d"], Take(cp, [M("d", 3), M("c", 2)]).Select(m => m.EntryId));
    }

    [Fact]
    public void Survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var cp = new Checkpoint { Last = T0 };
            Take(cp, [M("a", 1)]);
            Checkpoint.Save(path, cp);
            Assert.Empty(Take(Checkpoint.Load<Checkpoint>(path), [M("a", 1)]));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void First_start_does_not_replay_the_inbox()
    {
        var cp = Checkpoint.Load<Checkpoint>(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Empty(Take(cp, [new Mail("old", DateTime.Now.AddDays(-1), "x@y.z", "s")]));
    }

    [Fact]
    public void Matches_mail_triggers_on_sender_ignoring_case()
    {
        Trigger[] triggers =
        [
            new("triage", "mail", "/triage-mail", From: ["alice@example.com", "bob@example.com"]),
            new("other", "mail", "/other", From: ["someone@else.com"]),
            new("timesheet", "schedule", "/timesheet", Schedule: "monthly:28 09:00"),
        ];
        Assert.Equal(["triage"], MailTrigger.Matching(triggers, M("a", 1, "ALICE@example.COM")).Select(t => t.Name));
        Assert.Empty(MailTrigger.Matching(triggers, M("a", 1, "nobody@example.com")));
    }
}
