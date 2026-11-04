using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Butler.Tests;

// Against a local HTTP server answering like GitHub's releases/latest.
public sealed class ReleaseTests : IDisposable
{
    readonly HttpListener server = new();
    readonly string url, root;
    readonly string dir = Directory.CreateTempSubdirectory("butler-release").FullName;
    static readonly string Suffix = Platform.Current.Shell.ReleaseSuffix;
    const string tag = "v1.3.0";
    int requests;

    public ReleaseTests()
    {
        var port = FreePort();
        root = $"http://127.0.0.1:{port}/";
        url = root + "repos/o0Zz/butler/releases/latest";
        server.Prefixes.Add(root);
        server.Start();
        _ = Serve();
    }

    async Task Serve()
    {
        while (server.IsListening)
        {
            HttpListenerContext c;
            try { c = await server.GetContextAsync(); } catch (Exception) { return; }
            Interlocked.Increment(ref requests);
            var ok = c.Request.UserAgent?.StartsWith("Butler") == true;
            var path = c.Request.Url!.AbsolutePath;
            var assets = string.Join(",", new[] { ".exe", "-linux-x64", "-osx-arm64" }.Select(s =>
                $$"""{"name":"Butler-1.3.0.80{{s}}","browser_download_url":"{{root}}download/Butler-1.3.0.80{{s}}"}"""));
            var body = Encoding.UTF8.GetBytes(path.StartsWith("/download/") ? "build " + path[10..]
                : $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/o0Zz/butler/releases/tag/{{tag}}","assets":[{{assets}}]}""");
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
        var r = await Release.Newer("1.2.0.57 (abc1234)", Suffix, url);
        Assert.Equal(new Release("1.3.0", "https://github.com/o0Zz/butler/releases/tag/v1.3.0", $"{root}download/Butler-1.3.0.80{Suffix}"), r);
    }

    [Fact]
    public async Task Install_puts_the_download_in_place_and_keeps_the_old_one_aside()
    {
        var exe = Path.Combine(dir, "Butler.exe");
        File.WriteAllText(exe, "old build");
        await (await Release.Newer("1.2.0.57", Suffix, url))!.Install(exe);
        Assert.Equal($"build Butler-1.3.0.80{Suffix}", File.ReadAllText(exe));
        Assert.Equal("old build", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public async Task A_release_without_this_systems_file_is_not_installed()
    {
        var exe = Path.Combine(dir, "Butler.exe");
        File.WriteAllText(exe, "old build");
        var r = (await Release.Newer("1.2.0.57", "-plan9", url))!;
        Assert.Null(r.Download);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Install(exe));
        Assert.Equal("old build", File.ReadAllText(exe));
        Assert.Equal(["Butler.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("1.3.0.80 (abc1234)")] // the same release, built from more commits
    [InlineData("1.4.0.90")]
    public async Task The_same_or_an_older_tag_is_nothing_new(string current) => Assert.Null(await Release.Newer(current, Suffix, url));

    [Fact]
    public async Task A_development_build_does_not_ask()
    {
        Assert.Null(await Release.Newer("0.0.0.12 (abc1234)", Suffix, url));
        Assert.Equal(0, requests); // GitHub not even asked
    }

    public void Dispose()
    {
        server.Close();
        Directory.Delete(dir, recursive: true);
    }
}
