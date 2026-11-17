using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class UpdaterTests
{
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private const string Zip = "https://example.test/dl/clauderoo-1.2.0-win-x64.zip";
    private const string Sha = "https://example.test/dl/clauderoo-1.2.0-win-x64.zip.sha256";

    private static string ReleaseJson() => $$"""
        {"tag_name":"v1.2.0","assets":[
          {"name":"notes.txt","browser_download_url":"https://example.test/dl/notes.txt"},
          {"name":"clauderoo-1.2.0-win-x64.zip.sha256","browser_download_url":"{{Sha}}"},
          {"name":"clauderoo-1.2.0-win-x64.zip","browser_download_url":"{{Zip}}"}]}
        """;

    private static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = z.CreateEntry("clauderoo.exe");
            using var w = new StreamWriter(e.Open());
            w.Write("exe-bytes");
        }
        return ms.ToArray();
    }

    private static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
    private static HttpResponseMessage Ok(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };

    private static FakeHttp Server(string shaText, byte[]? zip = null)
    {
        zip ??= MakeZip();
        return new FakeHttp(r => r.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/repos/me/clauderoo/releases/latest" => Ok(ReleaseJson()),
            Zip => Ok(zip),
            Sha => Ok(shaText),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Latest_PicksAssets()
    {
        var http = Server("x");
        var r = await new Updater(http, "me/clauderoo").LatestAsync(default);
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 2, 0), r!.Version);
        Assert.Equal("v1.2.0", r.Tag);
        Assert.Equal(Zip, r.ZipUrl.AbsoluteUri);
        Assert.Equal(Sha, r.ShaUrl.AbsoluteUri);
        var req = http.Requests[0];
        Assert.Equal("clauderoo", req.Headers.UserAgent.ToString());
        Assert.Null(req.Headers.Authorization);
    }

    [Fact]
    public async Task Latest_NoRelease_Null()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await new Updater(http, "me/clauderoo").LatestAsync(default));
    }

    [Fact]
    public void Version_TwoAndThreePart_Equal()
    {
        Assert.Equal(AppVersion.Parse("1.2.0"), AppVersion.Parse("1.2"));
        Assert.Equal(AppVersion.Parse("v1"), AppVersion.Parse("1.0.0"));
        Assert.True(AppVersion.Parse("1.2.1") > AppVersion.Parse("1.2"));
    }

    [Fact]
    public void Upgrade_NoRelease_VsNoAssets_AreDistinct()
    {
        var none = RunCli(new ReleaseHooks { Repo = "me/clauderoo", CurrentVersion = "1.0.0",
            Http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)) }, "upgrade", "--check");
        Assert.Contains("No release found.", none.o);
        var noAssets = RunCli(new ReleaseHooks { Repo = "me/clauderoo", CurrentVersion = "1.0.0",
            Http = new FakeHttp(_ => Ok("""{"tag_name":"v1.5.0","assets":[]}""")) }, "upgrade", "--check");
        Assert.DoesNotContain("No release found.", noAssets.o);
        Assert.Contains("has no clauderoo-*-win-x64.zip / .sha256 assets", noAssets.o);
    }

    [Fact]
    public async Task Latest_NoMatchingAssets_Null()
    {
        var http = new FakeHttp(_ => Ok("""{"tag_name":"v1.0.0","assets":[{"name":"a.zip","browser_download_url":"https://x.test/a.zip"}]}"""));
        Assert.Null(await new Updater(http, "me/clauderoo").LatestAsync(default));
    }

    private static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "clauderoo-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Download_ShaMismatch_Throws_NoInstall()
    {
        var tmp = Temp();
        try
        {
            var up = new Updater(Server(new string('0', 64) + "  clauderoo-1.2.0-win-x64.zip"), "me/clauderoo");
            var r = (await up.LatestAsync(default))!;
            var e = await Assert.ThrowsAsync<ClauderooException>(() => up.DownloadVerifiedAsync(r, tmp, default));
            Assert.Equal("Download failed verification; nothing was changed.", e.Message);
            Assert.Empty(Directory.GetFileSystemEntries(tmp)); // nothing extracted, nothing left behind
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Download_ShaMatch_Extracts()
    {
        var tmp = Temp();
        try
        {
            var zip = MakeZip();
            var up = new Updater(Server(Hex(zip).ToUpperInvariant() + " *clauderoo-1.2.0-win-x64.zip\n", zip), "me/clauderoo");
            var r = (await up.LatestAsync(default))!;
            var dir = await up.DownloadVerifiedAsync(r, tmp, default);
            Assert.Equal("exe-bytes", File.ReadAllText(Path.Combine(dir, "clauderoo.exe")));
        }
        finally { Directory.Delete(tmp, true); }
    }

    // CLI

    private static (int code, string o, string e) RunCli(ReleaseHooks? hooks, params string[] args)
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => new DateTime(2026, 11, 17));
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, hooks: hooks);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Version_Prints()
    {
        var hooks = new ReleaseHooks { CurrentVersion = "1.4.2" };
        Assert.Equal("1.4.2" + Environment.NewLine, RunCli(hooks, "version").o);
        Assert.Equal("1.4.2" + Environment.NewLine, RunCli(hooks, "--version").o);
    }

    [Fact]
    public void Upgrade_NoRepo_Message()
    {
        var r = RunCli(new ReleaseHooks { Repo = "" }, "upgrade");
        Assert.Equal(1, r.code);
        Assert.Contains("This build wasn't made from a GitHub release; pull and rerun install.", r.e);
    }

    [Fact]
    public void Upgrade_Check_SaysUpToDate()
    {
        var hooks = new ReleaseHooks { Repo = "me/clauderoo", CurrentVersion = "1.2.0", Http = Server("x") };
        var r = RunCli(hooks, "upgrade", "--check");
        Assert.Equal(0, r.code);
        Assert.Contains("up to date", r.o);
    }

    [Fact]
    public void Upgrade_Check_ReportsNewer_WithoutDownloading()
    {
        var http = Server("x");
        var r = RunCli(new ReleaseHooks { Repo = "me/clauderoo", CurrentVersion = "1.1.0", Http = http }, "upgrade", "--check");
        Assert.Equal(0, r.code);
        Assert.Contains("1.2.0", r.o);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void Upgrade_Newer_VerifiesThenRunsExtractedInstall()
    {
        var zip = MakeZip();
        var ran = new List<string>();
        var existed = false;
        var hooks = new ReleaseHooks
        {
            Repo = "me/clauderoo", CurrentVersion = "1.1.0", Http = Server(Hex(zip), zip),
            RunInstall = exe => { ran.Add(exe); existed = File.Exists(exe); return 0; },
        };
        var r = RunCli(hooks, "upgrade");
        Assert.Equal(0, r.code);
        var exe = Assert.Single(ran);
        Assert.EndsWith("clauderoo.exe", exe);
        Assert.True(existed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(exe))!)); // temp cleaned up
    }

    [Fact]
    public void Upgrade_BadHash_ExitsOne_NoInstall()
    {
        var ran = 0;
        var hooks = new ReleaseHooks
        {
            Repo = "me/clauderoo", CurrentVersion = "1.1.0", Http = Server(new string('0', 64)),
            RunInstall = _ => { ran++; return 0; },
        };
        var r = RunCli(hooks, "upgrade");
        Assert.Equal(1, r.code);
        Assert.Contains("Download failed verification; nothing was changed.", r.e);
        Assert.Equal(0, ran);
    }
}
