namespace Butler.Tests;

// The real keychain (macOS) or secret service (Linux, skipped without secret-tool and a running keyring), under a throwaway name.
public class UnixSecretsTests
{
    [SkippableFact]
    public void A_secret_is_written_read_back_and_deleted()
    {
        ISecrets secrets = OperatingSystem.IsMacOS() ? new Keychain() : new SecretTool();
        Skip.If(secrets is SecretTool && !Works(), "secret-tool or a secret service is missing");
        var target = $"ButlerTest/{Guid.NewGuid()}";
        try
        {
            Assert.Null(secrets.Read(target));
            secrets.Write(target, "tok-é-123");
            Assert.Equal("tok-é-123", secrets.Read(target));
            secrets.Write(target, "new");
            Assert.Equal("new", secrets.Read(target));
        }
        finally { secrets.Delete(target); }
        Assert.Null(secrets.Read(target));
        secrets.Delete(target); // already gone: no error
    }

    static bool Works()
    {
        try { return Command.Run("secret-tool", ["search", "service", "ButlerTest-probe"]).Exit is 0 or 1; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
