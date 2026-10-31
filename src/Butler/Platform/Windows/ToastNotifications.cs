using Microsoft.Toolkit.Uwp.Notifications;

namespace Butler;

// Windows toasts. Clicking a toast or one of its buttons starts or reaches Butler with arguments handled in OnToast.
public sealed class ToastNotifications : INotifications
{
    public void Show(RunRecord run)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("run", run.Id)
            .AddText($"{run.Trigger}: {run.Status}")
            .AddText(run.Question ?? run.Summary ?? "");
        if (run.Status == RunStatus.Waiting)
        {
            builder.AddButton(new ToastButton().SetContent("Yes").AddArgument("run", run.Id).AddArgument("answer", "yes"));
            builder.AddButton(new ToastButton().SetContent("No").AddArgument("run", run.Id).AddArgument("answer", "no"));
            builder.AddButton(new ToastButton().SetContent("Open").AddArgument("run", run.Id));
        }
        builder.Show();
    }

    public void Error(string title, string message) => new ToastContentBuilder().AddText(title).AddText(message).Show();

    public bool StartedByClick()
    {
        ToastNotificationManagerCompat.OnActivated += OnToast;
        if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated()) return false;
        Thread.Sleep(3000); // OnActivated fires on another thread; give it time to start what it needs
        return true;
    }

    static void OnToast(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("run", out var runId)) return;
        if (args.TryGetValue("answer", out var answer)) Program.Start("answer", runId, answer);
        else Program.Start(); // opens the dashboard, or brings it up if the tray app already runs
    }
}
