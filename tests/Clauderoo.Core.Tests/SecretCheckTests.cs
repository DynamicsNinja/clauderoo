using System.Net;
using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class SecretCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);
    private const string Token = "sk-ant-oat01-SECRETCANARY-abc123";
    private const string Key = "sk-ant-api03-KEYCANARY-xyz789";

    private sealed class FakeKeyTester : IApiKeyTester
    {
        public (bool? valid, string? error) Result { get; set; } = (true, null);
        public List<(string key, Uri baseUrl)> Calls { get; } = [];
        public Task<(bool? valid, string? error)> TestAsync(string apiKey, Uri baseUrl, CancellationToken ct)
        {
            Calls.Add((apiKey, baseUrl));
            return Task.FromResult(Result);
        }
    }

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeSecretStore Keys = new();
        public FakeSecretStore Tokens = new();
        public FakeKeyTester Tester = new();
        public FakeClaude Claude = new();
        public ProfileManager Pm = null!;
        public string Dir => Home.Paths.ProfileDir("work");
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make(string provider)
    {
        var c = new Ctx();
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, c.Keys, new FakeGcloud(), c.Tokens)
        {
            HelperExe = @"C:\x\clauderoo.exe", KeyTester = c.Tester, ClaudeRunner = c.Claude,
        };
        c.Pm.Init();
        c.Pm.Add("work");
        if (provider == Providers.Token) c.Pm.SetProvider("work", Providers.Token, token: Token);
        else c.Pm.SetProvider("work", Providers.ApiKey, apiKey: Key);
        return c;
    }

    private static DateTimeOffset DaysAgo(int days) => new DateTimeOffset(Now).AddDays(-days);

    // ---- saved-at metadata (DPAPI store) ----

    [Fact]
    public void Dpapi_SavesSavedAt_InSidecarWithoutSecret()
    {
        using var home = new TempHome();
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var store = new DpapiSecretStore(DpapiSecretStore.TokenFileName, "token") { Clock = () => at };
        store.Save(home.Dir, Token);
        Assert.Equal(at, store.SavedAt(home.Dir));
        var meta = Path.Combine(home.Dir, DpapiSecretStore.TokenFileName + DpapiSecretStore.MetaSuffix);
        Assert.DoesNotContain("SECRETCANARY", File.ReadAllText(meta));
        Assert.Equal(Token, store.Load(home.Dir)); // the blob itself is unchanged
        store.Delete(home.Dir);
        Assert.False(File.Exists(meta));
        Assert.Null(store.SavedAt(home.Dir));
    }

    [Fact]
    public void Dpapi_OldBlobWithoutSidecar_FallsBackToWriteTime()
    {
        using var home = new TempHome();
        var store = new DpapiSecretStore();
        store.Save(home.Dir, Key);
        File.Delete(Path.Combine(home.Dir, DpapiSecretStore.FileName + DpapiSecretStore.MetaSuffix));
        var t = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(home.Dir, DpapiSecretStore.FileName), t);
        Assert.Equal(new DateTimeOffset(t), store.SavedAt(home.Dir));
        Assert.Equal(Key, store.Load(home.Dir));
    }

    [Fact]
    public void Dpapi_DamagedSidecar_FallsBackToWriteTime()
    {
        using var home = new TempHome();
        var store = new DpapiSecretStore();
        store.Save(home.Dir, Key);
        File.WriteAllText(Path.Combine(home.Dir, DpapiSecretStore.FileName + DpapiSecretStore.MetaSuffix), "{not json");
        Assert.NotNull(store.SavedAt(home.Dir));
    }

    // ---- Check (offline) with the token's age ----

    [Fact]
    public void Check_FreshToken_ShowsDate_NoWarning()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(100);
        var r = c.Pm.Check("work");
        Assert.True(r.Ok);
        Assert.False(r.Verified);
        Assert.Null(r.Warning);
        Assert.StartsWith("Token saved — not verified.", r.Message);
        Assert.Contains("(100 days ago)", r.Message);
        Assert.Empty(c.Pm.TokenAgeProblems());
    }

    [Fact]
    public void Check_TokenNearExpiry_Warns()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(340);
        var r = c.Pm.Check("work");
        Assert.True(r.Ok);
        Assert.NotNull(r.Warning);
        Assert.Contains("expires around", r.Warning);
        Assert.Contains("claude setup-token", r.Warning);
        Assert.Contains(r.Warning!, r.Message);
        var p = Assert.Single(c.Pm.TokenAgeProblems());
        Assert.Equal("work", p.Profile);
        Assert.Contains("expires soon", p.Title);
        Assert.Contains(p.Detail, c.Pm.Status().Problems);
    }

    [Fact]
    public void Check_TokenOverAYear_ProbablyExpired()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(400);
        Assert.Contains("probably expired", c.Pm.Check("work").Warning);
        Assert.Contains("probably expired", Assert.Single(c.Pm.TokenAgeProblems()).Title);
    }

    [Fact]
    public void TokenWarnAfterDays_FromConfig()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(200);
        Assert.Null(c.Pm.Check("work").Warning);
        var cfg = c.Pm.Config;
        cfg.TokenWarnAfterDays = 180;
        ConfigStore.Save(c.Home.Paths, cfg);
        Assert.NotNull(c.Pm.Check("work").Warning);
        Assert.Contains("\"tokenWarnAfterDays\": 180", File.ReadAllText(c.Home.Paths.ConfigFile));
    }

    [Fact]
    public void Check_UnknownSavedAt_KeepsOldMessage()
    {
        using var c = Make(Providers.Token);
        Assert.Equal(new CheckResult(true, "Token saved — not verified. Start a session to test it.", false), c.Pm.Check("work"));
    }

    [Fact]
    public void Check_ApiKey_NeverWarnsAboutAge()
    {
        using var c = Make(Providers.ApiKey);
        c.Keys.Times[c.Dir] = DaysAgo(900);
        var r = c.Pm.Check("work");
        Assert.Null(r.Warning);
        Assert.Contains("Saved on", r.Message);
    }

    // ---- live API key check ----

    [Fact]
    public async Task LiveKey_Accepted()
    {
        using var c = Make(Providers.ApiKey);
        var reports = new List<CheckItem>();
        var items = await c.Pm.CheckSecretAsync("work", default, new SyncProgress(reports));
        Assert.Equal(["saved", "live"], items.Select(i => i.Key));
        Assert.All(items, i => Assert.Equal(CheckStatus.Ok, i.Status));
        var (key, url) = Assert.Single(c.Tester.Calls);
        Assert.Equal(Key, key);
        Assert.Equal("https://api.anthropic.com/", url.ToString());
        Assert.Equal([CheckStatus.Pending, CheckStatus.Pending], reports.Take(2).Select(r => r.Status));
        Assert.Empty(c.Claude.Calls); // no claude process, no quota
    }

    [Fact]
    public async Task LiveKey_Rejected_Fails_NoSecretInText()
    {
        using var c = Make(Providers.ApiKey);
        c.Tester.Result = (false, $"HTTP 401 for {Key}");
        var items = await c.Pm.CheckSecretAsync("work");
        var live = items.Single(i => i.Key == "live");
        Assert.Equal(CheckStatus.Fail, live.Status);
        Assert.Contains("cr provider work apikey", live.Hint);
        Assert.DoesNotContain("KEYCANARY", string.Join("\n", items.Select(CheckItemFormatter.Format)));
    }

    [Fact]
    public async Task LiveKey_Unreachable_Warns()
    {
        using var c = Make(Providers.ApiKey);
        c.Tester.Result = (null, "HttpRequestException: no route");
        Assert.Equal(CheckStatus.Warn, (await c.Pm.CheckSecretAsync("work")).Single(i => i.Key == "live").Status);
    }

    [Fact]
    public async Task LiveKey_UsesProfileBaseUrl_RefusesPlainHttp()
    {
        using var c = Make(Providers.ApiKey);
        c.Pm.SetEnv("work", [new("ANTHROPIC_BASE_URL", "https://proxy.example.com/anthropic")], []);
        await c.Pm.CheckSecretAsync("work");
        Assert.Equal("https://proxy.example.com/anthropic/", Assert.Single(c.Tester.Calls).baseUrl.ToString());

        c.Pm.SetEnv("work", [new("ANTHROPIC_BASE_URL", "http://proxy.example.com")], []);
        var items = await c.Pm.CheckSecretAsync("work");
        Assert.Equal(CheckStatus.Warn, items.Single(i => i.Key == "live").Status);
        Assert.Single(c.Tester.Calls); // the key was not sent over plain http
    }

    [Fact]
    public async Task Live_NoSecretSaved_Fails()
    {
        using var c = Make(Providers.ApiKey);
        c.Keys.Delete(c.Dir);
        var items = await c.Pm.CheckSecretAsync("work");
        Assert.Equal(CheckStatus.Fail, items[0].Status);
        Assert.Equal(CheckStatus.Skipped, items[1].Status);
        Assert.Empty(c.Tester.Calls);
    }

    [Fact]
    public async Task Live_RefusesOtherProviders()
    {
        using var c = Make(Providers.ApiKey);
        c.Pm.SetProvider("work", Providers.Login);
        await Assert.ThrowsAsync<ClauderooException>(() => c.Pm.CheckSecretAsync("work"));
    }

    // ---- live token check ----

    [Fact]
    public async Task LiveToken_RunsOneHaikuPrompt_TokenInEnvOnly()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(10);
        var items = await c.Pm.CheckSecretAsync("work");
        Assert.Equal(["saved", "age", "claude", "live"], items.Select(i => i.Key));
        Assert.All(items, i => Assert.Equal(CheckStatus.Ok, i.Status));
        var (args, configDir) = Assert.Single(c.Claude.Calls);
        Assert.Equal(c.Dir, configDir);
        Assert.Contains("haiku", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain(args, a => a.Contains("CANARY"));
        Assert.Equal(Token, Assert.Single(c.Claude.Envs)[ProfileManager.TokenEnvVar]);
    }

    [Fact]
    public async Task LiveToken_Failure_ScrubsToken()
    {
        using var c = Make(Providers.Token);
        c.Claude.Responder = _ => (1, "", $"401 invalid token {Token}");
        var items = await c.Pm.CheckSecretAsync("work");
        var live = items.Single(i => i.Key == "live");
        Assert.Equal(CheckStatus.Fail, live.Status);
        Assert.DoesNotContain("CANARY", live.Detail);
        Assert.Contains("***", live.Detail);
        Assert.Equal(CheckStatus.Skipped, items.Single(i => i.Key == "age").Status); // unknown age
    }

    [Fact]
    public async Task LiveToken_NoClaude_SkipsProbe()
    {
        using var c = Make(Providers.Token);
        c.Claude.Path = null;
        var items = await c.Pm.CheckSecretAsync("work");
        Assert.Equal(CheckStatus.Fail, items.Single(i => i.Key == "claude").Status);
        Assert.Equal(CheckStatus.Skipped, items.Single(i => i.Key == "live").Status);
        Assert.Empty(c.Claude.Calls);
    }

    [Fact]
    public async Task LiveToken_OldToken_AgeRowFails()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(370);
        Assert.Equal(CheckStatus.Fail, (await c.Pm.CheckSecretAsync("work")).Single(i => i.Key == "age").Status);
    }

    // ---- the HTTP tester ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Last = r;
            return Task.FromResult(f(r));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    public async Task HttpTester_MapsStatus(HttpStatusCode status, bool? expected)
    {
        var h = new Handler(_ => new HttpResponseMessage(status));
        var (valid, _) = await new HttpApiKeyTester(h).TestAsync(Key, new Uri("https://api.anthropic.com/"), default);
        Assert.Equal(expected, valid);
        Assert.Equal(HttpMethod.Get, h.Last!.Method);
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1", h.Last.RequestUri!.ToString());
        Assert.Equal(Key, h.Last.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.Last.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task HttpTester_ErrorMessage_ScrubsKey()
    {
        var h = new Handler(_ => throw new HttpRequestException($"failed with {Key}"));
        var (valid, error) = await new HttpApiKeyTester(h).TestAsync(Key, new Uri("https://api.anthropic.com/"), default);
        Assert.Null(valid);
        Assert.DoesNotContain("KEYCANARY", error);
    }

    // ---- CLI ----

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Check_NotLive_SuggestsLive()
    {
        using var c = Make(Providers.ApiKey);
        var r = Run(c.Pm, "check", "work");
        Assert.Equal(0, r.code);
        Assert.Contains("cr check work --live", r.o);
        Assert.Empty(c.Tester.Calls);
    }

    [Fact]
    public void Cli_CheckLive_Json()
    {
        using var c = Make(Providers.ApiKey);
        c.Tester.Result = (false, "HTTP 401: the key was rejected");
        var r = Run(c.Pm, "check", "work", "--live", "--json");
        Assert.Equal(1, r.code);
        var j = JsonNode.Parse(r.o)!;
        Assert.Equal(1, j["schemaVersion"]!.GetValue<int>());
        Assert.False(j["ok"]!.GetValue<bool>());
        Assert.Equal(["saved", "live"], j["items"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()));
        Assert.Equal("fail", j["items"]![1]!["status"]!.GetValue<string>());
        Assert.DoesNotContain("KEYCANARY", r.o + r.e);
    }

    [Fact]
    public void Cli_CheckLive_Token_Text()
    {
        using var c = Make(Providers.Token);
        var r = Run(c.Pm, "check", "work", "--live");
        Assert.Equal(0, r.code);
        Assert.Contains("one tiny prompt", r.o);
        Assert.Contains("✓ Token answers a test prompt", r.o);
        Assert.DoesNotContain("CANARY", r.o + r.e);
    }

    [Fact]
    public void Cli_Check_Json_HasWarning()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(350);
        var j = JsonNode.Parse(Run(c.Pm, "check", "work", "--json").o)!;
        Assert.Contains("expires around", j["warning"]!.GetValue<string>());
    }

    [Fact]
    public void Cli_Status_ListsTokenAge()
    {
        using var c = Make(Providers.Token);
        c.Tokens.Times[c.Dir] = DaysAgo(350);
        Assert.Contains("work's Claude token", Run(c.Pm, "status").e);
    }

    private sealed class SyncProgress(List<CheckItem> into) : IProgress<CheckItem>
    {
        public void Report(CheckItem value) { lock (into) into.Add(value); }
    }
}
