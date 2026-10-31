namespace Butler.Tests;

// Against the real Windows Credential Manager, under a throwaway name.
public class SecretsTests
{
    [Fact]
    public void A_secret_is_written_read_back_and_deleted()
    {
        var secrets = new CredentialManager();
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
}
