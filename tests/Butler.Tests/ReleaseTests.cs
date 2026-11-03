using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Butler.Tests;

// Against a local HTTP server answering like GitHub's releases/latest.
public sealed class ReleaseTests : IDisposable
{
    readonly HttpListener server = new();
    readonly string url;
    const string tag = "v1.3.0";

    public ReleaseTests()
    {
        var port = FreePort();
        url = $"http://127.0.0.1:{port}/repos/o0Zz/butler/releases/latest";
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Serve();
    }

    async Task Serve()
    {
        while (server.IsListening)
        {
            HttpListenerContext c;
            try { c = await server.GetContextAsync(); } catch (Exception) { return; }
            var ok = c.Request.UserAgent?.StartsWith("Butler") == true;
            var body = Encoding.UTF8.GetBytes($$"""{"tag_name":"{{tag}}","html_url":"https://github.com/o0Zz/butler/releases/tag/{{tag}}","assets":[]}""");
            c.Response.StatusCode = ok ? 200 : 403; // as GitHub without a User-Agent
            await c.Response.OutputStream.WriteAsync(body);
            c.Response.Close();
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task A_newer_tag_is_a_new_version_with_its_page()
    {
        var r = await Release.Newer("1.2.0.57 (abc1234)", url);
        Assert.Equal(new Release("1.3.0", "https://github.com/o0Zz/butler/releases/tag/v1.3.0"), r);
    }

    [Theory]
    [InlineData("1.3.0.80 (abc1234)")] // the same release, built from more commits
    [InlineData("1.4.0.90")]
    public async Task The_same_or_an_older_tag_is_nothing_new(string current) => Assert.Null(await Release.Newer(current, url));

    [Fact]
    public async Task A_development_build_does_not_ask()
    {
        server.Stop(); // a request would fail
        Assert.Null(await Release.Newer("0.0.0.12 (abc1234)", url));
    }

    public void Dispose() => server.Close();
}
