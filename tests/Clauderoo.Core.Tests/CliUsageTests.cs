using System.Net;
using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliUsageTests
{
    private const string Token = "sk-ant-oat01-FAKE-cli-token-0123456789";
    private static readonly DateTime Clock = new(2026, 11, 17, 12, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Handler(Func<HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(f());
        }
    }

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage-fixture.json"));

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        pm.Init();
        pm.Add("work");
        SignIn(pm.ConfigDirOf("work"));
        return (pm, home);
    }

    private static void SignIn(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            $$"""
            { "claudeAiOauth": { "accessToken": "{{Token}}", "refreshToken": "refresh-FAKE",
              "expiresAt": {{Now.AddHours(2).ToUnixTimeMilliseconds()}}, "subscriptionType": "max",
              "rateLimitTier": "default_claude_max_5x" } }
            """);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, Handler h, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        using var client = new UsageClient(h, () => Now, "1.0");
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
            hookCancel: _ => new NoopDisposable(), usage: client);
        return (code, o.ToString(), e.ToString());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public void Usage_PrintsTable()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.OK, Fixture())), "usage", "work");
        Assert.Equal(0, r.code);
        Assert.Contains("work (Max 5x)", r.o);
        Assert.Contains("5h", r.o);
        Assert.Contains("42%", r.o);
        Assert.Contains("7d", r.o);
        Assert.Contains("63%", r.o);
        Assert.Contains("Opus", r.o);
        Assert.Contains("71%", r.o);
        Assert.DoesNotContain(Token, r.o + r.e);
        // an Ok result is cached
        Assert.Equal("work", new UsageCache(home.Paths).Load()["work"].Profile);
    }

    [Fact]
    public void Usage_RateLimited_FallsBackToCache_ShowsAsOf()
    {
        var (pm, home) = Make();
        using var _h = home;
        var fetched = new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);
        new UsageCache(home.Paths).Put(new UsageReading("work", fetched, "Max 5x", [new UsageWindow("5h", 33, null)]));

        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.TooManyRequests)), "usage", "work");
        Assert.Equal(0, r.code);
        Assert.Contains("33%", r.o);
        Assert.Contains($"as of {fetched.ToLocalTime():HH:mm}", r.o);
        Assert.Equal(33, new UsageCache(home.Paths).Load()["work"].Windows[0].Percent); // cache kept
    }

    [Fact]
    public void Usage_Failed_NoCache_Exit1_NoToken()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.InternalServerError, $"echo {Token}")), "usage", "work");
        Assert.Equal(1, r.code);
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void Usage_Json()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.OK, Fixture())), "usage", "work", "--json");
        Assert.Equal(0, r.code);
        Assert.StartsWith("{", r.o.TrimStart());
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("schemaVersion", root.EnumerateObject().First().Name);
        var p = Assert.Single(root.GetProperty("profiles").EnumerateArray());
        Assert.Equal("work", p.GetProperty("name").GetString());
        Assert.Equal("ok", p.GetProperty("status").GetString());
        Assert.Equal("Max 5x", p.GetProperty("plan").GetString());
        Assert.Equal("2026-11-17T12:00:00Z", p.GetProperty("fetchedAt").GetString());
        var w = p.GetProperty("windows");
        Assert.Equal("5h", w[0].GetProperty("name").GetString());
        Assert.Equal(42, w[0].GetProperty("percent").GetDouble());
        Assert.Equal("2026-11-17T15:40:00Z", w[0].GetProperty("resetsAt").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("message").ValueKind); // only failed and unavailable rows say why
        Assert.DoesNotContain(Token, r.o);
    }

    [Fact]
    public void Usage_Json_Failed_HasScrubbedMessage()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.InternalServerError, $"echo {Token}")), "usage", "work", "--json");
        Assert.Equal(1, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var p = Assert.Single(doc.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal("failed", p.GetProperty("status").GetString());
        var message = p.GetProperty("message").GetString();
        Assert.Contains("HTTP 500", message);
        Assert.DoesNotContain(Token, message);
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void Usage_Unavailable_FallsBackToCache_JsonHasMessage()
    {
        var (pm, home) = Make();
        using var _h = home;
        var fetched = new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);
        new UsageCache(home.Paths).Put(new UsageReading("work", fetched, "Max", [new UsageWindow("5h", 33, null)]));
        var h = new Handler(() => Resp(HttpStatusCode.OK, "[]")); // not the expected object: Unavailable

        var text = Run(pm, h, "usage", "work");
        Assert.Equal(0, text.code);
        Assert.Contains("33%", text.o);
        Assert.Contains($"as of {fetched.ToLocalTime():HH:mm}", text.o);

        var r = Run(pm, h, "usage", "work", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var p = Assert.Single(doc.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal("unavailable", p.GetProperty("status").GetString());
        Assert.Equal(33, p.GetProperty("windows")[0].GetProperty("percent").GetDouble());
        Assert.Equal("2026-11-17T09:30:00Z", p.GetProperty("fetchedAt").GetString());
        Assert.Contains("unexpected", p.GetProperty("message").GetString());
    }

    [Fact]
    public void Usage_Unavailable_NoCache_Exit1()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.OK, "[]")), "usage", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("unexpected", r.o);
    }

    [Fact]
    public void Usage_Json_UnknownProfile_JsonErrorOnStderr_Exit1()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.OK, Fixture())), "usage", "nope", "--json");
        Assert.Equal(1, r.code);
        Assert.Equal("", r.o);
        using var doc = JsonDocument.Parse(r.e);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("Unknown profile", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Usage_Json_UnexpectedError_JsonShaped_Exit2()
    {
        var (pm, home) = Make();
        using var _h = home;
        var o = new StringWriter();
        var e = new StringWriter();
        using var client = new UsageClient(new Handler(() => Resp(HttpStatusCode.OK, Fixture())), () => Now, "1.0");
        var code = Commands.Run(["usage", "work", "--json"], o, e, pm, _ => 0, _ => 0, wtAvailable: true,
            hookCancel: _ => throw new InvalidOperationException("kaboom"), usage: client);
        Assert.Equal(2, code);
        Assert.Equal("", o.ToString());
        using var doc = JsonDocument.Parse(e.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("kaboom", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Usage_Expired_SaysStartSession()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.Unauthorized)), "usage", "work");
        Assert.Equal(0, r.code);
        Assert.Contains("expired", r.o);
        Assert.Contains("cr start work", r.o);
    }

    [Fact]
    public void Usage_NoNameShowsAllProfiles_VertexNotTracked()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "p", Region = "r" });
        var h = new Handler(() => Resp(HttpStatusCode.OK, Fixture()));
        var r = Run(pm, h, "usage");
        Assert.Equal(0, r.code);
        Assert.Contains("personal", r.o);
        Assert.Contains("Usage isn't tracked for Google Vertex AI.", r.o);
    }

    [Fact]
    public void Usage_Json_SyncNotice_StdoutIsPureJson()
    {
        var (pm, home) = Make();
        using var _h = home;
        // Home's settings.json changes and the profile's copy was edited by hand: the pre-sync replaces the copy, keeps
        // a .bak-<timestamp> and prints a notice. Under --json that notice must go to stderr, not into the payload.
        var homeDir = pm.Config.HomeDir;
        File.WriteAllText(Path.Combine(homeDir, "settings.json"), """{ "theme": "dark" }""");
        File.WriteAllText(Path.Combine(pm.ConfigDirOf("work"), "settings.json"), """{ "edited": "by hand" }""");

        var r = Run(pm, new Handler(() => Resp(HttpStatusCode.OK, Fixture())), "usage", "work", "--json");
        Assert.Equal(0, r.code);
        Assert.Contains(".bak-", r.e); // the notice was printed, on stderr
        Assert.DoesNotContain(".bak-", r.o);
        using var doc = JsonDocument.Parse(r.o); // stdout is the payload and nothing else
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("work", Assert.Single(doc.RootElement.GetProperty("profiles").EnumerateArray()).GetProperty("name").GetString());
        Assert.NotEmpty(Directory.GetFiles(pm.ConfigDirOf("work"), "settings.json.bak-*"));
    }

    [Fact]
    public void Usage_TokenProfile_NotTracked_NeverPeekedOrFetched()
    {
        const string stored = "sk-ant-oat01-FAKE-stored-token-for-ci";
        var (pm, home) = Make();
        using var _h = home;
        pm.Add("ci");
        pm.SetProvider("ci", Providers.Token, token: stored);
        // A sign-in file in the token profile's folder, held open with no sharing: a peek would fail to read it and
        // report "failed" instead of "notTracked".
        var dir = pm.ConfigDirOf("ci");
        SignIn(dir);
        var h = new Handler(() => Resp(HttpStatusCode.OK, Fixture()));
        (int code, string o, string e) text, json;
        using (new FileStream(Path.Combine(dir, ".credentials.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            text = Run(pm, h, "usage", "ci");
            json = Run(pm, h, "usage", "ci", "--json");
        }

        Assert.Equal(0, h.Calls); // never fetched
        Assert.Equal(0, text.code);
        Assert.Contains("Usage isn't tracked for Claude token.", text.o);
        Assert.Equal(0, json.code);
        using var doc = JsonDocument.Parse(json.o);
        var p = Assert.Single(doc.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal("notTracked", p.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("fetchedAt").ValueKind);
        Assert.Empty(p.GetProperty("windows").EnumerateArray());
        Assert.DoesNotContain(stored, text.o + text.e + json.o + json.e);
        Assert.False(new UsageCache(home.Paths).Load().ContainsKey("ci"));

        // `cr list` shows no usage column value for it either, even with a cached reading under its name.
        new UsageCache(home.Paths).Put(new UsageReading("ci", Now, "Pro", [new UsageWindow("5h", 42, null)]));
        var list = Run(pm, h, "list");
        Assert.DoesNotContain("42%", list.o.Split('\n').Single(l => l.Contains("ci ")));
    }

    [Fact]
    public void List_ShowsCachedUsage()
    {
        var (pm, home) = Make();
        using var _h = home;
        new UsageCache(home.Paths).Put(new UsageReading("work", Now, "Pro",
            [new UsageWindow("5h", 42, null), new UsageWindow("7d", 63, null)]));
        var h = new Handler(() => throw new InvalidOperationException("no http"));
        var r = Run(pm, h, "list");
        Assert.Equal(0, r.code);
        Assert.Equal(0, h.Calls); // list reads the cache only
        var work = r.o.Split('\n').Single(l => l.Contains("work"));
        Assert.Contains("42%/63%", work);
        var personal = r.o.Split('\n').Single(l => l.Contains("personal"));
        Assert.DoesNotContain("%", personal);
    }

    [Fact]
    public void List_NoCache_HasNoUsageColumn()
    {
        var (pm, home) = Make();
        using var _h = home;
        var h = new Handler(() => throw new InvalidOperationException("no http"));
        var r = Run(pm, h, "list");
        Assert.DoesNotContain("%", r.o);
        Assert.Equal(0, h.Calls);
    }
}
