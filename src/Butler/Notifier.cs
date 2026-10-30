using Microsoft.Toolkit.Uwp.Notifications;

namespace Butler;

// Windows toasts. Clicking a toast or one of its buttons starts or reaches Butler with arguments
// handled in Program.OnToast.
public static class Notifier
{
    public static void Show(RunRecord run)
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

    public static void Error(string title, string message) =>
        new ToastContentBuilder().AddText(title).AddText(message).Show();
}
