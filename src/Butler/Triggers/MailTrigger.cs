using System.Diagnostics;

namespace Butler;

public sealed record Mail(string EntryId, DateTime Received, string From, string Subject);

// "mail": a mail from one of the From addresses arrived, in any mail folder of the mailbox (Outlook classic, Windows).
// Checkpoint: <state>/mail.json, so mails received while Butler was closed are handled at the next start.
public sealed class MailTrigger : TriggerKind
{
    public override string Name => "mail";
    public override string Label => T("Mail");
    public override FormField[] Form => [new("from", T("From"), "list", T("Sender addresses, one per line (exact match, any case)"))];
    public override string[] Variables => ["from", "subject", "received"];
    public override string? Context => "Triggered by this mail: from ${from}, subject \"${subject}\", received ${received}.";

    public override void Validate(Trigger t)
    {
        if (t.From is not { Length: > 0 }) throw new FormatException($"Trigger '{t.Name}': from is empty");
    }

    public override string Describe(Trigger t) => T("When a mail arrives");

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir, Platform platform)
    {
        if (platform.Mailbox.Unavailable is { } why) throw new InvalidOperationException(why);
        var path = Path.Combine(stateDir, "mail.json");
        var checkpoint = Checkpoint.Load<Checkpoint>(path);
        var runs = checkpoint.TakeNew(platform.Mailbox.ReadSince(checkpoint.Last), m => m.EntryId, m => m.Received)
            .SelectMany(mail => Matching(triggers, mail).Select(t => (t, Values(mail))))
            .ToList();
        Checkpoint.Save(path, checkpoint);
        return runs;
    }

    public static IEnumerable<Trigger> Matching(IEnumerable<Trigger> triggers, Mail mail) =>
        triggers.Where(t => t.On == "mail" && t.From!.Any(f => f.Equals(mail.From, StringComparison.OrdinalIgnoreCase)));

    static Dictionary<string, string> Values(Mail mail) =>
        new() { ["from"] = mail.From, ["subject"] = mail.Subject, ["received"] = $"{mail.Received:yyyy-MM-dd HH:mm}" };
}
