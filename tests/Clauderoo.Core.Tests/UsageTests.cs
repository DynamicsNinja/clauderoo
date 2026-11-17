using System.Net;

namespace Clauderoo.Core.Tests;

public class UsageTests
{
    private const string Token = "sk-ant-oat01-FAKE-test-token-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProfileEntry Login = new();

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage-fixture.json"));

    // A fake .credentials.json in a temp folder (never the real ~/.claude).
    private static string WriteCreds(TempHome h, DateTimeOffset expires, string? subscription = "max",
        string? tier = "default_claude_max_5x")
    {
        var dir = Path.Combine(h.Dir, "cfg");
        Directory.CreateDirectory(dir);
        var sub = subscription == null ? "" : $", \"subscriptionType\": \"{subscription}\"";
        var rt = tier == null ? "" : $", \"rateLimitTier\": \"{tier}\"";
        File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            $$"""
            { "claudeAiOauth": { "accessToken": "{{Token}}", "refreshToken": "refresh-FAKE-999",
              "expiresAt": {{expires.ToUnixTimeMilliseconds()}}, "scopes": ["user:inference"]{{sub}}{{rt}} },
              "mcpOAuth": { "x": { "accessToken": "other-FAKE" } } }
            """);
        return dir;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            Last = r;
            return Task.FromResult(f(r));
        }
    }

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body) };

    private static UsageClient Client(HttpMessageHandler h) => new(h, () => Now, "1.2.3");

    // --- CredentialPeek ---

    [Fact]
    public void Peek_NoFile_NotSignedIn()
    {
        using var h = new TempHome();
        var (peek, token) = CredentialPeek.Read(Path.Combine(h.Dir, "nothing"), Now);
        Assert.Equal(PeekStatus.NotSignedIn, peek.Status);
        Assert.Null(token);
    }

    [Fact]
    public void Peek_Expired()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddSeconds(30)); // within the 60 s margin
        var (peek, token) = CredentialPeek.Read(dir, Now);
        Assert.Equal(PeekStatus.Expired, peek.Status);
        Assert.Null(token);
    }

    [Fact]
    public void Peek_Ok_ReturnsPlan()
    {
        using var h = new TempHome();
        var exp = Now.AddHours(2);
        var dir = WriteCreds(h, exp);
        var (peek, token) = CredentialPeek.Read(dir, Now);
        Assert.Equal(PeekStatus.Ok, peek.Status);
        Assert.Equal("Max 5x", peek.Plan);
        Assert.Equal(exp.ToUnixTimeMilliseconds(), peek.ExpiresAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(Token, token);
        Assert.DoesNotContain(Token, peek.ToString());
    }

    [Fact]
    public void Peek_ProPlan_NoTierSuffix()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2), "pro", "default_claude_ai");
        Assert.Equal("Pro", CredentialPeek.Read(dir, Now).Peek.Plan);
    }

    [Fact]
    public void Peek_BadJson_Unreadable()
    {
        using var h = new TempHome();
        var dir = Path.Combine(h.Dir, "cfg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), "{ not json");
        var (peek, token) = CredentialPeek.Read(dir, Now);
        Assert.Equal(PeekStatus.Unreadable, peek.Status);
        Assert.Null(token);
    }

    [Fact]
    public void Peek_NoClaudeOauth_NotSignedIn()
    {
        using var h = new TempHome();
        var dir = Path.Combine(h.Dir, "cfg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), """{ "mcpOAuth": {} }""");
        Assert.Equal(PeekStatus.NotSignedIn, CredentialPeek.Read(dir, Now).Peek.Status);
    }

    // --- UsageClient ---

    [Fact]
    public async Task Fetch_Vertex_NotTracked_NoHttp()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        var r = await Client(handler).FetchAsync("work", dir, new ProfileEntry { Provider = Providers.Vertex }, default);
        var nt = Assert.IsType<UsageResult.NotTracked>(r);
        Assert.Equal(Providers.Vertex, nt.Provider);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_SendsHeaders()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var handler = new Handler(_ => Resp(HttpStatusCode.OK, Fixture()));
        await Client(handler).FetchAsync("personal", dir, Login, default);
        var req = handler.Last!;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(req.Headers.GetValues("anthropic-beta")));
        Assert.Equal("clauderoo/1.2.3", string.Join(" ", req.Headers.GetValues("User-Agent")));
    }

    [Fact]
    public async Task Fetch_ParsesWindowsAndLimits()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var r = await Client(new Handler(_ => Resp(HttpStatusCode.OK, Fixture()))).FetchAsync("personal", dir, Login, default);
        var reading = Assert.IsType<UsageResult.Ok>(r).Reading;
        Assert.Equal("personal", reading.Profile);
        Assert.Equal(Now, reading.FetchedAt);
        Assert.Equal("Max 5x", reading.Plan);
        Assert.Equal(3, reading.Windows.Count);

        var five = reading.Windows[0];
        Assert.Equal("5h", five.Name);
        Assert.Equal(42.0, five.Percent);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 15, 40, 0, TimeSpan.Zero).AddTicks(1234560), five.ResetsAt);

        var seven = reading.Windows[1];
        Assert.Equal("7d", seven.Name);
        Assert.Equal(63.0, seven.Percent);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), seven.ResetsAt);

        var opus = reading.Windows[2];
        Assert.Equal("Opus", opus.Name);
        Assert.Equal(71.0, opus.Percent);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), opus.ResetsAt);
    }

    [Fact]
    public async Task Fetch_MissingFields_StillOk()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var body = """{ "five_hour": null, "seven_day": { "utilization": 10.5, "resets_at": null }, "limits": [ { "kind": "x", "scope": { "model": { "display_name": null } } }, { "percent": 5 } ] }""";
        var r = await Client(new Handler(_ => Resp(HttpStatusCode.OK, body))).FetchAsync("personal", dir, Login, default);
        var w = Assert.Single(Assert.IsType<UsageResult.Ok>(r).Reading.Windows);
        Assert.Equal("7d", w.Name);
        Assert.Equal(10.5, w.Percent);
        Assert.Null(w.ResetsAt);

        var empty = await Client(new Handler(_ => Resp(HttpStatusCode.OK, "{}"))).FetchAsync("personal", dir, Login, default);
        Assert.Empty(Assert.IsType<UsageResult.Ok>(empty).Reading.Windows);
    }

    [Fact]
    public async Task Fetch_401_Expired()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var r = await Client(new Handler(_ => Resp(HttpStatusCode.Unauthorized, "{}"))).FetchAsync("personal", dir, Login, default);
        Assert.IsType<UsageResult.Expired>(r);
    }

    [Fact]
    public async Task Fetch_ExpiredCredentials_NoHttp()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddMinutes(-5));
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        Assert.IsType<UsageResult.Expired>(await Client(handler).FetchAsync("personal", dir, Login, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_NoCredentials_NotSignedIn()
    {
        using var h = new TempHome();
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        var r = await Client(handler).FetchAsync("personal", Path.Combine(h.Dir, "none"), Login, default);
        Assert.IsType<UsageResult.NotSignedIn>(r);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_429_RetryAfter()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var handler = new Handler(_ =>
        {
            var m = Resp(HttpStatusCode.TooManyRequests);
            m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1200));
            return m;
        });
        var r = await Client(handler).FetchAsync("personal", dir, Login, default);
        Assert.Equal(TimeSpan.FromSeconds(1200), Assert.IsType<UsageResult.RateLimited>(r).RetryAfter);
    }

    [Fact]
    public async Task Fetch_BadJson_Unavailable()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var r = await Client(new Handler(_ => Resp(HttpStatusCode.OK, "<html>nope"))).FetchAsync("personal", dir, Login, default);
        Assert.IsType<UsageResult.Unavailable>(r);
        var arr = await Client(new Handler(_ => Resp(HttpStatusCode.OK, "[1,2]"))).FetchAsync("personal", dir, Login, default);
        Assert.IsType<UsageResult.Unavailable>(arr);
    }

    [Fact]
    public async Task UsageClient_ErrorBodyEchoingToken_IsScrubbed()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var handler = new Handler(_ => Resp(HttpStatusCode.InternalServerError,
            $"{{\"error\":\"bad header Authorization: Bearer {Token}\"}}"));
        var r = await Client(handler).FetchAsync("personal", dir, Login, default);
        var f = Assert.IsType<UsageResult.Failed>(r);
        Assert.DoesNotContain(Token, f.Message);
        Assert.DoesNotContain(Token, r.ToString());
        Assert.Contains("500", f.Message);
    }

    [Fact]
    public async Task Fetch_HandlerThrowsWithToken_IsScrubbed()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var handler = new Handler(_ => throw new HttpRequestException($"connection reset while sending {Token}"));
        var r = await Client(handler).FetchAsync("personal", dir, Login, default);
        var f = Assert.IsType<UsageResult.Failed>(r);
        Assert.DoesNotContain(Token, f.Message);
    }

    [Fact]
    public async Task Fetch_DoesNotWriteCredentialsFile()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var file = Path.Combine(dir, ".credentials.json");
        var past = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, past);
        var bytes = File.ReadAllBytes(file);
        var before = Directory.GetFileSystemEntries(dir);

        await Client(new Handler(_ => Resp(HttpStatusCode.OK, Fixture()))).FetchAsync("personal", dir, Login, default);
        await Client(new Handler(_ => Resp(HttpStatusCode.Unauthorized))).FetchAsync("personal", dir, Login, default);

        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(past, File.GetLastWriteTimeUtc(file));
        Assert.Equal(before, Directory.GetFileSystemEntries(dir));
    }

    // --- UsageCache ---

    [Fact]
    public async Task Cache_RoundTrip_NoTokenInFile()
    {
        using var h = new TempHome();
        var dir = WriteCreds(h, Now.AddHours(2));
        var r = await Client(new Handler(_ => Resp(HttpStatusCode.OK, Fixture()))).FetchAsync("personal", dir, Login, default);
        var reading = Assert.IsType<UsageResult.Ok>(r).Reading;

        var cache = new UsageCache(h.Paths);
        cache.Put(reading);

        var text = File.ReadAllText(Path.Combine(h.Paths.Root, "usage.json"));
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("refresh-FAKE", text);

        var loaded = new UsageCache(h.Paths).Load()["personal"];
        Assert.Equal(reading.Profile, loaded.Profile);
        Assert.Equal(reading.FetchedAt, loaded.FetchedAt);
        Assert.Equal(reading.Plan, loaded.Plan);
        Assert.Equal(reading.Windows, loaded.Windows);
    }

    [Fact]
    public void Cache_RenameRemove()
    {
        using var h = new TempHome();
        var cache = new UsageCache(h.Paths);
        cache.Put(new UsageReading("a", Now, "Pro", [new UsageWindow("5h", 1, null)]));
        cache.Put(new UsageReading("b", Now, null, []));

        cache.Rename("a", "c");
        var after = cache.Load();
        Assert.False(after.ContainsKey("a"));
        Assert.Equal("c", after["c"].Profile);
        Assert.Equal("Pro", after["c"].Plan);

        cache.Remove("b");
        Assert.Equal(["c"], cache.Load().Keys.ToArray());

        cache.Rename("missing", "x"); // no-op
        cache.Remove("missing");
        Assert.Equal(["c"], cache.Load().Keys.ToArray());
    }

    [Fact]
    public void Cache_CorruptFile_LoadsEmpty()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(Path.Combine(h.Paths.Root, "usage.json"), "{ garbage");
        var cache = new UsageCache(h.Paths);
        Assert.Empty(cache.Load());
        cache.Put(new UsageReading("a", Now, null, []));
        Assert.Single(cache.Load());
    }

    // --- ProfileManager hooks ---

    [Fact]
    public void ConfigDirOf_HomeAndProfile()
    {
        using var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now.DateTime);
        pm.Init();
        pm.Add("work");
        Assert.Equal(h.Paths.DefaultHomeDir, pm.ConfigDirOf("personal"));
        Assert.Equal(h.Paths.ProfileDir("work"), pm.ConfigDirOf("work"));
        Assert.Throws<ClauderooException>(() => pm.ConfigDirOf("nope"));
    }

    [Fact]
    public void ProfileManager_RenameRemove_UpdateUsageCache()
    {
        using var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now.DateTime);
        pm.Init();
        pm.Add("work");
        pm.Add("side");
        var cache = new UsageCache(h.Paths);
        cache.Put(new UsageReading("work", Now, null, []));
        cache.Put(new UsageReading("side", Now, null, []));

        pm.Rename("work", "job");
        Assert.Equal(["job", "side"], cache.Load().Keys.Order().ToArray());

        pm.Remove("side");
        Assert.Equal(["job"], cache.Load().Keys.ToArray());
    }

    // --- Secrets ---

    [Fact]
    public void Secrets_Scrub()
    {
        Assert.Equal("a *** b ***", Secrets.Scrub("a tok b tok", "tok"));
        Assert.Equal("a tok", Secrets.Scrub("a tok", null));
        Assert.Equal("a tok", Secrets.Scrub("a tok", ""));
    }
}
