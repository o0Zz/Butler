namespace Butler;

public sealed partial record Platform
{
    private static partial Platform Create() => OperatingSystem.IsMacOS()
        ? new(new OsascriptNotifications(), new Keychain(), new LaunchAgent(), new MacShell(), new NoMailbox())
        : new(new NotifySend(), new SecretTool(), new AutostartFile(), new LinuxShell(), new NoMailbox());
}
