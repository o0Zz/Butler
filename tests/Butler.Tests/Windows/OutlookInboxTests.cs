namespace Butler.Tests;

// Reads the real Outlook inbox (read-only). Skipped when Outlook classic is not running.
public class OutlookInboxTests
{
    [SkippableFact]
    public void Reads_recent_mails_newest_first_with_smtp_senders()
    {
        Skip.IfNot(OutlookInbox.IsRunning, "Outlook is not running");
        var mails = new OutlookInbox().ReadSince(DateTime.Now.AddDays(-7));
        Assert.NotEmpty(mails);
        Assert.Equal(mails.OrderByDescending(m => m.Received).Select(m => m.EntryId), mails.Select(m => m.EntryId));
        Assert.All(mails, m => Assert.True(m.Received >= DateTime.Now.AddDays(-7)));
        Assert.DoesNotContain(mails, m => m.From.StartsWith("/O=", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void Reads_mails_moved_out_of_the_inbox_but_not_sent_ones()
    {
        Skip.IfNot(OutlookInbox.IsRunning, "Outlook is not running");
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)!;
        dynamic ns = app.GetNamespace("MAPI");
        var defaults = new[] { 3, 4, 5, 6, 16, 23 }.Select(f => (string)ns.GetDefaultFolder(f).FolderPath).ToList();

        // Newest mail of a mail folder next to the Inbox (where rules move mails), within 30 days.
        dynamic? moved = null;
        dynamic root = ns.GetDefaultFolder(6).Parent;
        for (int i = 1; i <= root.Folders.Count && moved == null; i++)
        {
            dynamic folder = root.Folders[i];
            if (folder.DefaultItemType != 0 || defaults.Contains((string)folder.FolderPath)) continue;
            moved = NewestMail(folder);
        }
        Skip.If(moved == null, "No recent mail outside the Inbox");
        Assert.Contains(new OutlookInbox().ReadSince(moved!.ReceivedTime), (Predicate<Mail>)(m => m.EntryId == (string)moved.EntryID));

        dynamic? sent = NewestMail(ns.GetDefaultFolder(5));
        Skip.If(sent == null, "No recent sent mail");
        Assert.DoesNotContain(new OutlookInbox().ReadSince(sent!.ReceivedTime), (Predicate<Mail>)(m => m.EntryId == (string)sent.EntryID));
    }

    static dynamic? NewestMail(dynamic folder)
    {
        dynamic items = folder.Items;
        items.Sort("[ReceivedTime]", true);
        for (dynamic? item = items.GetFirst(); item != null; item = items.GetNext())
            if (item.Class == 43) return item.ReceivedTime >= DateTime.Now.AddDays(-30) ? item : null;
        return null;
    }
}
