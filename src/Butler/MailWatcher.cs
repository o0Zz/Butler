using System.Diagnostics;
using System.Text.Json;

namespace Butler;

public sealed record Mail(string EntryId, DateTime Received, string From, string Subject);

// Remembers the last mail handled, so a poll (or a start after downtime) only sees mails not handled yet.
// Persisted in <home>/state/mail.json. On first use it starts from "now": the existing inbox is not replayed.
public sealed class MailCheckpoint
{
    public DateTime Last { get; set; }
    public List<string> IdsAtLast { get; set; } = [];

    public static MailCheckpoint Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<MailCheckpoint>(File.ReadAllText(path))! : new() { Last = DateTime.Now };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    // Takes mails received at or after Last, returns the new ones oldest first and moves the checkpoint.
    public List<Mail> TakeNew(IEnumerable<Mail> candidates)
    {
        var fresh = candidates
            .Where(m => m.Received > Last || (m.Received == Last && !IdsAtLast.Contains(m.EntryId)))
            .OrderBy(m => m.Received)
            .ToList();
        if (fresh.Count == 0) return fresh;

        var newest = fresh[^1].Received;
        IdsAtLast = newest == Last ? [.. IdsAtLast, .. fresh.Where(m => m.Received == newest).Select(m => m.EntryId)]
                                   : [.. fresh.Where(m => m.Received == newest).Select(m => m.EntryId)];
        Last = newest;
        return fresh;
    }

    public static IEnumerable<Trigger> Matching(IEnumerable<Trigger> triggers, Mail mail) =>
        triggers.Where(t => t.On == "mail" && t.From!.Any(f => f.Equals(mail.From, StringComparison.OrdinalIgnoreCase)));

    // The values of the mail variables, as `Butler.exe run` arguments.
    public static string[] VariablesFor(Mail mail) =>
        ["from=" + mail.From, "subject=" + mail.Subject, $"received={mail.Received:yyyy-MM-dd HH:mm}"];
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
