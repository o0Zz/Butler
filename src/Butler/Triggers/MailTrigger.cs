using System.Diagnostics;

namespace Butler;

public sealed record Mail(string EntryId, DateTime Received, string From, string Subject);

// "mail": a mail from one of the From addresses arrived, in any mail folder of Outlook classic.
// Checkpoint: <state>/mail.json, so mails received while Butler was closed are handled at the next start.
public sealed class MailTrigger : TriggerKind
{
    public override string Name => "mail";
    public override string Label => "Mail";
    public override FormField[] Form => [new("from", "From", "list", "Sender addresses, one per line (exact match, any case)")];
    public override string[] Variables => ["from", "subject", "received"];
    public override string? Context => "Triggered by this mail: from ${from}, subject \"${subject}\", received ${received}.";

    public override void Validate(Trigger t)
    {
        if (t.From is not { Length: > 0 }) throw new FormatException($"Trigger '{t.Name}': from is empty");
    }

    public override string Describe(Trigger t) => "When a mail arrives";

    public override List<(Trigger Trigger, Dictionary<string, string> Values)> Poll(List<Trigger> triggers, Settings settings, string stateDir)
    {
        if (!OutlookInbox.IsRunning) throw new InvalidOperationException("Outlook classic is not running: mail triggers wait for it");
        var path = Path.Combine(stateDir, "mail.json");
        var checkpoint = Checkpoint.Load<Checkpoint>(path);
        var runs = checkpoint.TakeNew(OutlookInbox.ReadSince(checkpoint.Last), m => m.EntryId, m => m.Received)
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

// Reads the mail folders of the default Outlook classic mailbox through COM. Only call from an STA thread.
public static class OutlookInbox
{
    public static bool IsRunning => Process.GetProcessesByName("OUTLOOK").Length > 0;

    // Deleted, Outbox, Sent, Drafts, Junk: never hold mails to react to. Skipped with their subfolders.
    static readonly int[] SkippedFolders = [3, 4, 5, 16, 23];

    // Mails received at or after `since`, newest first. Every mail folder is read, not only the Inbox:
    // rules move mails out of it.
    public static List<Mail> ReadSince(DateTime since)
    {
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)!;
        dynamic ns = app.GetNamespace("MAPI");
        var skipped = new HashSet<string>();
        foreach (var f in SkippedFolders) skipped.Add((string)ns.GetDefaultFolder(f).FolderPath);
        var mails = new List<Mail>();
        Collect(ns.GetDefaultFolder(6).Parent, since, skipped, mails); // 6 = olFolderInbox, its parent is the mailbox root
        return [.. mails.OrderByDescending(m => m.Received)];
    }

    static void Collect(dynamic folder, DateTime since, HashSet<string> skipped, List<Mail> mails)
    {
        if (skipped.Contains((string)folder.FolderPath)) return;
        if (folder.DefaultItemType == 0) // 0 = olMailItem; skip calendar, contacts, tasks
        {
            // Restrict compares to the minute in the local format; the exact check is below.
            dynamic items = folder.Items.Restrict($"[ReceivedTime] >= '{since.AddMinutes(-1):g}'");
            for (dynamic? item = items.GetFirst(); item != null; item = items.GetNext())
            {
                if (item.Class != 43) continue; // 43 = olMail; skip meeting requests, reports
                DateTime received = item.ReceivedTime;
                if (received >= since) mails.Add(new Mail(item.EntryID, received, SenderAddress(item), item.Subject ?? ""));
            }
        }
        dynamic subfolders = folder.Folders;
        for (int i = 1; i <= subfolders.Count; i++) Collect(subfolders[i], since, skipped, mails);
    }

    // Exchange senders come as "/O=EXCHANGELABS/..." instead of an SMTP address.
    static string SenderAddress(dynamic item)
    {
        if (item.SenderEmailType != "EX") return item.SenderEmailAddress ?? "";
        try { return item.Sender.GetExchangeUser()?.PrimarySmtpAddress ?? item.SenderEmailAddress ?? ""; }
        catch { return item.SenderEmailAddress ?? ""; }
    }
}
