using System.Diagnostics;

namespace Butler;

// Reads the mail folders of the default Outlook classic mailbox through COM. Only call from an STA thread.
public sealed class OutlookInbox : IMailbox
{
    public static bool IsRunning => Process.GetProcessesByName("OUTLOOK").Length > 0;

    public string? Unavailable => IsRunning ? null : "Outlook classic is not running: mail triggers wait for it";

    // Deleted, Outbox, Sent, Drafts, Junk: never hold mails to react to. Skipped with their subfolders.
    static readonly int[] SkippedFolders = [3, 4, 5, 16, 23];

    // Mails received at or after `since`, newest first. Every mail folder is read, not only the Inbox:
    // rules move mails out of it.
    public List<Mail> ReadSince(DateTime since)
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
