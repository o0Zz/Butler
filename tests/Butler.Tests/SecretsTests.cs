namespace Butler.Tests;

// Against the real Windows Credential Manager, under a throwaway name.
public class SecretsTests
{
    [Fact]
    public void A_secret_is_written_read_back_and_deleted()
    {
        var target = $"ButlerTest/{Guid.NewGuid()}";
        try
        {
            Assert.Null(Secrets.Read(target));
            Secrets.Write(target, "tok-é-123");
            Assert.Equal("tok-é-123", Secrets.Read(target));
            Secrets.Write(target, "new");
            Assert.Equal("new", Secrets.Read(target));
        }
        finally { Secrets.Delete(target); }
        Assert.Null(Secrets.Read(target));
        Secrets.Delete(target); // already gone: no error
    }
}
