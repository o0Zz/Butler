namespace Butler;

public sealed partial record Platform
{
    private static partial Platform Create() => new(new ToastNotifications(), new CredentialManager(), new RunKeyStartup(), new WindowsShell(), new OutlookInbox(), new WebView2(), new WindowsHotkeys());
}
