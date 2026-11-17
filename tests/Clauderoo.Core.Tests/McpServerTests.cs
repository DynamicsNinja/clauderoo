using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class McpServerTests
{
    private const string Token = "sk-ant-oat01-LEAKCANARY-7f3e9a1b2c4d5e6f8a9b";
    private const string HomeToken = "sk-ant-oat01-HOMECANARY-1a2b3c4d5e6f7a8b9c0d";
    private const string StoredToken = "sk-ant-oat01-TOKNCANARY-9z8y7x6w5v4u3t2s1r0q";
    private static readonly DateTime Clock = new(2026, 11, 17, 12, 0, 0);
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(f(r));
        }
    }

    private static HttpResponseMessage OkUsage() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{ "five_hour": { "utilization": 42 }, "seven_day": { "utilization": 63 } }"""),
    };

    private sealed class Runner
    {
        public LaunchSpec? Spec;
        public string? Stdin;
        public TimeSpan Timeout;
        public int Calls;
        public Func<LaunchSpec, (int, string, string)> Answer = _ => (0, "done", "");
        public Func<LaunchSpec, CancellationToken, Task<(int, string, string)>>? AnswerAsync;

        public Task<(int code, string stdout, string stderr)> Run(LaunchSpec spec, string stdin, TimeSpan timeout, CancellationToken ct)
        {
            Spec = spec;
            Stdin = stdin;
            Timeout = timeout;
            Calls++;
            return AnswerAsync != null ? AnswerAsync(spec, ct) : Task.FromResult(Answer(spec));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TempHome Home = new();
        public readonly ProfileManager Pm;
        public readonly Handler Http;
        public readonly UsageClient Usage;
        public readonly Runner Runner = new();
        public readonly StringWriter Log = new();
        public DateTimeOffset Now = Start;
        public string? CallerProfile;
        public readonly Dictionary<string, string> EnvVars = new(StringComparer.OrdinalIgnoreCase);
        public readonly string Proj;
        public readonly McpServer Server;

        public Fixture(Func<HttpRequestMessage, HttpResponseMessage>? answer = null, string claude = "claude.exe")
        {
            Pm = new ProfileManager(Home.Paths, new FakeEnv(), () => Clock);
            Pm.Init();
            Pm.Add("work");
            Pm.Add("ci");
            Pm.SetProvider("ci", Providers.Token, token: StoredToken);
            PlantCredential(Pm.ConfigDirOf("work"), Token);
            PlantCredential(Pm.ConfigDirOf(Pm.Config.HomeProfileName!), HomeToken);
            Proj = Directory.CreateDirectory(Path.Combine(Home.Dir, "proj")).FullName;
            Http = new Handler(answer ?? (_ => OkUsage()));
            Usage = new UsageClient(Http, () => Now, "1.0");
            Server = new McpServer(Pm, Usage, Runner.Run, now: () => Now,
                getEnv: n => n == "CLAUDEROO_PROFILE" ? CallerProfile : EnvVars.GetValueOrDefault(n),
                claudePath: claude, defaultCwd: Proj, log: Log);
        }

        public void PlantCredential(string dir, string token)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".credentials.json"),
                $$"""
                { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "refresh-{{token}}",
                  "expiresAt": {{Start.AddDays(30).ToUnixTimeMilliseconds()}}, "subscriptionType": "max" } }
                """);
        }

        public void Dispose()
        {
            Usage.Dispose();
            Home.Dispose();
        }
    }

    /// <summary>Feeds the lines to the server and returns every line it wrote, each checked to be a JSON object.</summary>
    // stdin stays open until every request (and every line that isn't JSON) has been answered, because EOF cancels
    // in-flight delegates, as a client going away should.
    private static async Task<List<JsonObject>> Talk(McpServer s, params string[] lines)
    {
        var inner = new StringWriter();
        var output = TextWriter.Synchronized(inner);
        string Snapshot() { lock (output) return inner.ToString(); }
        var expected = lines.Count(l =>
        {
            if (string.IsNullOrWhiteSpace(l)) return false;
            try { return JsonNode.Parse(l) is not JsonObject o || o.ContainsKey("id"); }
            catch (System.Text.Json.JsonException) { return true; }
        });
        var input = new PipeReader();
        var run = s.RunAsync(input, output, CancellationToken.None);
        foreach (var l in lines) input.Send(l);
        var sw = Stopwatch.StartNew();
        while (Snapshot().Count(c => c == '\n') < expected && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        input.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var outLines = Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
        return outLines.Select(l => Assert.IsType<JsonObject>(JsonNode.Parse(l))).ToList();
    }

    private static string Call(int id, string tool, object? args = null) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = args == null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)),
            },
        }.ToJsonString();

    private static async Task<JsonObject> CallOne(Fixture f, string tool, object? args = null)
    {
        var r = Assert.Single(await Talk(f.Server, Call(1, tool, args)));
        return r["result"]!.AsObject();
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();
    private static bool IsError(JsonObject result) => result["isError"]?.GetValue<bool>() == true;

    [Fact]
    public async Task Initialize_ReturnsServerInfo()
    {
        using var f = new Fixture();
        var r = Assert.Single(await Talk(f.Server,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal("2.0", r["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, r["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("clauderoo", r["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.IsType<JsonObject>(r["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task ToolsList_HasFourTools()
    {
        using var f = new Fixture();
        var r = Assert.Single(await Talk(f.Server, """{"jsonrpc":"2.0","id":"a","method":"tools/list"}"""));
        Assert.Equal("a", r["id"]!.GetValue<string>());
        var tools = r["result"]!["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        Assert.Equal(["list_profiles", "usage", "set_default", "delegate"], tools.Select(t => t["name"]!.GetValue<string>()));
        foreach (var t in tools) Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
        var del = tools.Single(t => t["name"]!.GetValue<string>() == "delegate");
        Assert.Equal(["profile", "prompt"], del["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(del["inputSchema"]!["properties"]!["cwd"]);
        var desc = del["description"]!.GetValue<string>();
        Assert.Contains("quota", desc);
        Assert.Contains("permissions", desc);
        Assert.Equal(["profile"], tools.Single(t => t["name"]!.GetValue<string>() == "set_default")["inputSchema"]!["required"]!
            .AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task ListProfiles_ReturnsProfilesNoToken()
    {
        using var f = new Fixture();
        f.Pm.SetDefault("work");
        new UsageCache(f.Home.Paths).Put(new UsageReading("work", Start.AddMinutes(-30), "max",
            [new UsageWindow("5h", 42, null)]));
        var result = await CallOne(f, "list_profiles");
        Assert.False(IsError(result));
        var json = Text(result);
        var profiles = JsonNode.Parse(json)!["profiles"]!.AsArray().Select(p => p!.AsObject()).ToList();
        Assert.Equal(3, profiles.Count);
        var work = profiles.Single(p => p["name"]!.GetValue<string>() == "work");
        Assert.True(work["isDefault"]!.GetValue<bool>());
        Assert.Equal("login", work["provider"]!.GetValue<string>());
        Assert.Equal(0, work["runningSessions"]!.GetValue<int>());
        Assert.Equal(42, work["cachedUsage"]!["windows"]![0]!["percent"]!.GetValue<double>());
        Assert.Equal("token", profiles.Single(p => p["name"]!.GetValue<string>() == "ci")["provider"]!.GetValue<string>());
        Assert.Equal(["name", "provider", "account", "detail", "isDefault", "runningSessions", "cachedUsage"],
            work.Select(kv => kv.Key));
        Assert.DoesNotContain("CANARY", json);
        Assert.Equal(0, f.Http.Calls); // no quota, no network
    }

    [Fact]
    public async Task SetDefault_ChangesDefault()
    {
        using var f = new Fixture();
        var result = await CallOne(f, "set_default", new { profile = "work" });
        Assert.False(IsError(result));
        Assert.Equal("work", f.Pm.Config.DefaultProfile);
        Assert.Contains("work", Text(result));

        var bad = await CallOne(f, "set_default", new { profile = "nope" });
        Assert.True(IsError(bad));
        Assert.Contains("nope", Text(bad));
    }

    // A default chosen through the MCP tool is the user's choice, like the tray's or `cr default`: auto-switch holds it.
    [Fact]
    public async Task SetDefault_Mcp_NotesManualHold()
    {
        using var f = new Fixture();
        Assert.Null(f.Pm.AutoSwitchState.Load().ManualHold);
        var result = await CallOne(f, "set_default", new { profile = "work" });
        Assert.False(IsError(result));
        Assert.Equal("work", f.Pm.Config.DefaultProfile);
        Assert.Equal("work", f.Pm.AutoSwitchState.Load().ManualHold);
    }

    [Fact]
    public async Task Delegate_BuildsArgs_StrictMcp_NoSkipPermissions()
    {
        using var f = new Fixture();
        var cwd = Directory.CreateDirectory(Path.Combine(f.Home.Dir, "proj")).FullName;
        f.Runner.Answer = _ => (0, "the answer", "a warning");
        var result = await CallOne(f, "delegate", new { profile = "work", prompt = "say \"hi\" & bye", cwd });
        Assert.False(IsError(result));
        Assert.Contains("the answer", Text(result));
        Assert.Contains("a warning", Text(result));

        var spec = f.Runner.Spec!;
        Assert.Equal("claude.exe", spec.FileName);
        Assert.Equal(FixedArgs, spec.Args);
        Assert.Equal("say \"hi\" & bye", f.Runner.Stdin);
        Assert.DoesNotContain(spec.Args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spec.Args, a => a.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(cwd, spec.WorkingDir);
        Assert.Equal("work", spec.Profile);
        Assert.Equal(f.Pm.ConfigDirOf("work"), spec.ConfigDirEnv);
        Assert.Equal(TimeSpan.FromMinutes(10), f.Runner.Timeout);
        Assert.False(spec.ExtraEnv!.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
    }

    [Fact]
    public async Task Delegate_TokenProfile_GetsTokenInEnvOnly_DefaultCwd()
    {
        using var f = new Fixture();
        var result = await CallOne(f, "delegate", new { profile = "ci", prompt = "x" });
        Assert.False(IsError(result));
        var spec = f.Runner.Spec!;
        Assert.Equal(StoredToken, spec.ExtraEnv!["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.DoesNotContain(spec.Args, a => a.Contains("CANARY"));
        Assert.Equal(f.Proj, spec.WorkingDir);
    }

    private static readonly string[] FixedArgs =
        ["-p", "--output-format", "text", "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}"""];

    // The prompt never reaches argv, so it can't be read as a claude option or cut the command line short.
    [Fact]
    public async Task Delegate_PromptNeverInArgs()
    {
        foreach (var prompt in new[] { "hi", "--permission-mode=bypassPermissions", "--mcp-config={\"mcpServers\":{}} --add-dir=C:\\" })
        {
            using var f = new Fixture();
            var r = await CallOne(f, "delegate", new { profile = "work", prompt });
            Assert.False(IsError(r));
            Assert.Equal(FixedArgs, f.Runner.Spec!.Args);
            Assert.Equal(FixedArgs, Launcher.StartInfo(f.Runner.Spec!).ArgumentList);
            Assert.Equal(prompt, f.Runner.Stdin);
        }

        using var g = new Fixture();
        foreach (var prompt in new[] { "a\u0000b", new string('x', 100 * 1024 + 1) })
        {
            var resp = Assert.Single(await Talk(g.Server, Call(1, "delegate", new { profile = "work", prompt })));
            var refused = resp["error"] != null || IsError(resp["result"]!.AsObject());
            Assert.True(refused);
        }
        Assert.Equal(0, g.Runner.Calls);
    }

    [Fact]
    public async Task Delegate_PromptSentOnStdin()
    {
        using var f = new Fixture();
        const string prompt = "line one\nline \"two\" & ünïcode %PATH% !x!";
        await CallOne(f, "delegate", new { profile = "work", prompt });
        Assert.Equal(prompt, f.Runner.Stdin);
        Assert.DoesNotContain(f.Runner.Spec!.Args, a => a.Contains("line"));
    }

    // Reading files in the working folder needs no approval, so it must never be one holding sign-ins or settings.
    [Fact]
    public async Task Delegate_CwdUnderClauderooOrClaude_Refused()
    {
        using var f = new Fixture();
        var cfg = f.Pm.Config;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "projects"));
        Directory.CreateDirectory(f.Home.Paths.IsolatedDir("work"));
        var link = Path.Combine(f.Proj, "sneaky");
        Junction.Create(link, f.Home.Paths.ProfileDir("work")); // a project-looking folder that is really a profile
        string[] refused =
        [
            link,
            f.Home.Paths.Root,
            f.Home.Paths.Root.ToUpperInvariant() + "\\",
            f.Home.Paths.ProfileDir("work"),
            f.Home.Paths.IsolatedDir("work"),
            cfg.HomeDir,
            Path.Combine(cfg.HomeDir, "projects"),
            Path.GetDirectoryName(cfg.HomeJson)!, // the user-profile folder itself
            Path.GetDirectoryName(cfg.HomeJson)! + "\\",
            Path.Combine(f.Proj, "..", ".claude"),
        ];
        foreach (var cwd in refused)
        {
            var r = await CallOne(f, "delegate", new { profile = "work", prompt = "x", cwd });
            Assert.True(IsError(r), cwd);
            Assert.Contains("can't run in", Text(r));
        }
        Assert.Equal(0, f.Runner.Calls);

        var ok = await CallOne(f, "delegate", new { profile = "work", prompt = "x", cwd = f.Proj });
        Assert.False(IsError(ok));
    }

    private static async Task AssertCwdRefused(Fixture f, string cwd)
    {
        var calls = f.Runner.Calls;
        var r = await CallOne(f, "delegate", new { profile = "work", prompt = "x", cwd });
        Assert.True(IsError(r), cwd);
        Assert.Contains("can't run in", Text(r));
        Assert.Equal(calls, f.Runner.Calls);
    }

    // A network folder can be anything (including a loopback admin share onto ~/.clauderoo), so it's never allowed.
    [Fact]
    public async Task Delegate_CwdUnc_Refused()
    {
        using var f = new Fixture();
        var drive = f.Proj[0];
        var rest = f.Proj[3..];
        await AssertCwdRefused(f, $@"\\localhost\{drive}$\{rest}");
        await AssertCwdRefused(f, $@"\\127.0.0.1\{char.ToLowerInvariant(drive)}$\{rest}");
        await AssertCwdRefused(f, $@"\\localhost\{drive}$\{f.Home.Paths.ProfileDir("work")[3..]}");
        await AssertCwdRefused(f, @"\\?\" + f.Proj);
    }

    // A folder whose real path can't be worked out (here a dangling junction) is refused, not checked by its name only.
    [Fact]
    public async Task Delegate_CwdUnresolvable_Refused()
    {
        using var f = new Fixture();
        var gone = Directory.CreateDirectory(Path.Combine(f.Home.Dir, "gone")).FullName;
        var link = Path.Combine(f.Proj, "dangling");
        Junction.Create(link, gone);
        Directory.Delete(gone);
        Assert.True(Directory.Exists(link)); // still looks like a folder
        await AssertCwdRefused(f, link);
    }

    // A profile folder that is itself a junction elsewhere (another drive, say) is protected at its real location too.
    [Fact]
    public async Task Delegate_ProtectedRootIsJunction_StillProtected()
    {
        using var f = new Fixture();
        var real = Directory.CreateDirectory(Path.Combine(f.Home.Dir, "otherdrive", "work")).FullName;
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var profileDir = f.Home.Paths.ProfileDir("work");
        Directory.Move(profileDir, profileDir + ".moved");
        Junction.Create(profileDir, real);
        try
        {
            await AssertCwdRefused(f, real);
            await AssertCwdRefused(f, Path.Combine(real, "sub"));
        }
        finally { Directory.Delete(profileDir); } // the junction only
    }

    // A folder above the protected ones (C:\, C:\Users) can read every profile's sign-in, so it's refused too.
    [Fact]
    public async Task Delegate_CwdAncestorOfProtected_Refused()
    {
        using var f = new Fixture();
        var userProfile = f.Home.Paths.UserProfile;
        await AssertCwdRefused(f, Path.GetDirectoryName(userProfile)!);
        await AssertCwdRefused(f, Path.GetPathRoot(userProfile)!);
        // The real user folder's parent too (C:\Users), whatever the test's home is.
        await AssertCwdRefused(f, Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!);
        // A project folder that is really a junction to an ancestor.
        var link = Path.Combine(f.Proj, "up");
        Junction.Create(link, Path.GetDirectoryName(userProfile)!);
        try { await AssertCwdRefused(f, link); }
        finally { Directory.Delete(link); }
        Assert.Equal(0, f.Runner.Calls);

        var ok = await CallOne(f, "delegate", new { profile = "work", prompt = "x", cwd = f.Proj });
        Assert.False(IsError(ok));
    }

    // gcloud's sign-in (Vertex profiles) and SSH keys are protected like Claude's own, by name and by real path.
    [Fact]
    public async Task Delegate_CwdGcloudOrSsh_Refused()
    {
        using var f = new Fixture();
        var appData = Directory.CreateDirectory(Path.Combine(f.Home.Dir, "AppData", "Roaming")).FullName;
        f.EnvVars["APPDATA"] = appData;
        var gcloud = Directory.CreateDirectory(Path.Combine(appData, "gcloud", "configurations")).Parent!.FullName;
        var ssh = Directory.CreateDirectory(Path.Combine(f.Home.Paths.UserProfile, ".ssh", "sub")).Parent!.FullName;
        var toGcloud = Path.Combine(f.Proj, "gc");
        var toSsh = Path.Combine(f.Proj, "keys");
        Junction.Create(toGcloud, gcloud);
        Junction.Create(toSsh, ssh);
        try
        {
            foreach (var cwd in new[] { gcloud, Path.Combine(gcloud, "configurations"), ssh, Path.Combine(ssh, "sub"),
                         toGcloud, toSsh, appData, Path.GetDirectoryName(appData)! })
                await AssertCwdRefused(f, cwd);
        }
        finally
        {
            Directory.Delete(toGcloud);
            Directory.Delete(toSsh);
        }
        Assert.Equal(0, f.Runner.Calls);
    }

    // A delegated run's launch spec drops the caller's provider, model and session variables; normal launches don't.
    [Fact]
    public async Task Delegate_SpecDropsCallerEnv()
    {
        using var f = new Fixture();
        f.Pm.SetEnv("work", [new("MY_PROFILE_SETTING", "v")], []);
        Assert.False(IsError(await CallOne(f, "delegate", new { profile = "ci", prompt = "x" })));
        var drop = f.Runner.Spec!.DropEnv!;
        foreach (var n in new[] { "CLAUDE_CODE_USE_VERTEX", "anthropic_vertex_project_id", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
                     "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "ANTHROPIC_MODEL", "CLAUDECODE",
                     "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "my_profile_setting" })
            Assert.True(drop(n), n);
        foreach (var n in new[] { "PATH", "USERPROFILE", "APPDATA", "CLAUDE_CONFIG_DIR", "HTTPS_PROXY" })
            Assert.False(drop(n), n);
        Assert.Equal(StoredToken, f.Runner.Spec.ExtraEnv!["CLAUDE_CODE_OAUTH_TOKEN"]); // the target's own still applies

        Assert.Null(Launcher.InPlace(f.Pm.Config, f.Home.Paths, "work", f.Proj, [], "claude.exe").DropEnv);
    }

    private static Func<LaunchSpec, CancellationToken, Task<(int, string, string)>> BlockUntil(Task release) =>
        async (_, ct) =>
        {
            await release.WaitAsync(ct);
            return (0, "finished", "");
        };

    [Fact]
    public async Task Delegate_ThirdConcurrentRun_Refused()
    {
        using var f = new Fixture();
        var release = new TaskCompletionSource();
        f.Runner.AnswerAsync = BlockUntil(release.Task);
        var input = new PipeReader();
        var inner = new StringWriter();
        var output = TextWriter.Synchronized(inner);
        string[] Lines() { lock (output) return inner.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
        async Task WaitLines(int n)
        {
            var sw = Stopwatch.StartNew();
            while (Lines().Length < n && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        }
        var run = f.Server.RunAsync(input, output, CancellationToken.None);
        input.Send(Call(1, "delegate", new { profile = "work", prompt = "a" }));
        input.Send(Call(2, "delegate", new { profile = "ci", prompt = "b" }));
        input.Send(Call(3, "delegate", new { profile = "work", prompt = "c" }));
        await WaitLines(1);
        var third = JsonNode.Parse(Assert.Single(Lines()))!.AsObject();
        Assert.Equal(3, third["id"]!.GetValue<int>());
        Assert.True(IsError(third["result"]!.AsObject()));
        Assert.Equal("2 delegated runs are already in progress; try again when one finishes.", Text(third["result"]!.AsObject()));
        Assert.Equal(2, f.Runner.Calls);

        release.SetResult(); // both finish; a new one is accepted again
        await WaitLines(3);
        input.Send(Call(4, "delegate", new { profile = "work", prompt = "d" }));
        await WaitLines(4);
        input.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var last = JsonNode.Parse(Lines()[3])!.AsObject();
        Assert.Equal(4, last["id"]!.GetValue<int>());
        Assert.False(IsError(last["result"]!.AsObject()));
        Assert.Equal(3, f.Runner.Calls);
    }

    [Fact]
    public async Task Delegate_DuplicateInflightId_Refused()
    {
        using var f = new Fixture();
        var cancelled = new TaskCompletionSource();
        f.Runner.AnswerAsync = BlockUntilCancelled(cancelled);
        var input = new PipeReader();
        var inner = new StringWriter();
        var output = TextWriter.Synchronized(inner);
        string Snapshot() { lock (output) return inner.ToString(); }
        var run = f.Server.RunAsync(input, output, CancellationToken.None);
        input.Send(Call(5, "delegate", new { profile = "work", prompt = "a" }));
        input.Send(Call(5, "delegate", new { profile = "ci", prompt = "b" }));
        input.Send("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""");
        var sw = Stopwatch.StartNew();
        while (Snapshot().Count(c => c == '\n') < 2 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        var lines = Snapshot().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(2, lines.Count);
        foreach (var l in lines)
        {
            Assert.Equal(5, l["id"]!.GetValue<int>());
            Assert.Equal(-32600, l["error"]!["code"]!.GetValue<int>());
        }
        Assert.Equal(1, f.Runner.Calls);
        Assert.Equal("work", f.Runner.Spec!.Profile);

        // The original run still owns the id: cancelling 5 stops it.
        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        input.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A run that is stopped while its stdin write is still blocked leaves no faulted task behind.
    [Fact]
    public async Task ProcessRunner_TimeoutWhileWritingStdin_NoUnobservedFault()
    {
        using var home = new TempHome();
        var faults = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> h = (_, e) =>
        {
            if (e.Exception.ToString().Contains("McpServer")) lock (faults) faults.Add(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += h;
        try
        {
            var runner = McpServer.ProcessRunner(new SessionTracker(home.Paths));
            var ping = Path.Combine(Environment.SystemDirectory, "ping.exe"); // never reads its stdin
            await Assert.ThrowsAsync<TimeoutException>(() =>
                runner(new LaunchSpec(ping, ["-n", "60", "127.0.0.1"], home.Dir, null), new string('x', 2_000_000),
                    TimeSpan.FromSeconds(1), CancellationToken.None));
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
        }
        finally { TaskScheduler.UnobservedTaskException -= h; }
        Assert.Empty(faults);
    }

    [Fact]
    public async Task Delegate_OutputScrubsTargetAccessToken()
    {
        using var f = new Fixture();
        const string Fresh = "sk-ant-oat01-FRESHCANARY-0000111122223333";
        f.Runner.Answer = spec =>
        {
            // claude prints the sign-in file, after refreshing it mid-run
            var before = File.ReadAllText(Path.Combine(spec.ConfigDirEnv!, ".credentials.json"));
            f.PlantCredential(spec.ConfigDirEnv!, Fresh);
            var after = File.ReadAllText(Path.Combine(spec.ConfigDirEnv!, ".credentials.json"));
            return (0, before + after, after);
        };
        var text = Text(await CallOne(f, "delegate", new { profile = "work", prompt = "print .credentials.json" }));
        Assert.Contains("accessToken", text); // the fake really echoed it
        Assert.DoesNotContain("LEAKCANARY", text);
        Assert.DoesNotContain("FRESHCANARY", text);
    }

    /// <summary>A stdin the test feeds line by line; EOF only when Complete is called.</summary>
    private sealed class PipeReader : TextReader
    {
        private readonly System.Threading.Channels.Channel<string> _ch = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public void Send(string line) => _ch.Writer.TryWrite(line);
        public void Complete() => _ch.Writer.TryComplete();
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try { return await _ch.Reader.ReadAsync(ct); }
            catch (System.Threading.Channels.ChannelClosedException) { return null; }
        }
        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    private static Func<LaunchSpec, CancellationToken, Task<(int, string, string)>> BlockUntilCancelled(TaskCompletionSource cancelled) =>
        async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
            return (0, "never", "");
        };

    [Fact]
    public async Task Delegate_CancelledRequest_NoResponse()
    {
        using var f = new Fixture();
        var cancelled = new TaskCompletionSource();
        f.Runner.AnswerAsync = BlockUntilCancelled(cancelled);
        var input = new PipeReader();
        var output = new StringWriter();
        var run = f.Server.RunAsync(input, output, CancellationToken.None);
        input.Send(Call(5, "delegate", new { profile = "work", prompt = "x" }));
        input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        input.Send("""{"jsonrpc":"2.0","id":6,"method":"tools/list"}""");
        input.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(6, JsonNode.Parse(line)!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Eof_CancelsInflightDelegates()
    {
        using var f = new Fixture();
        var cancelled = new TaskCompletionSource();
        f.Runner.AnswerAsync = BlockUntilCancelled(cancelled);
        var output = new StringWriter();
        await f.Server.RunAsync(new StringReader(Call(1, "delegate", new { profile = "work", prompt = "x" }) + "\n"), output,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal("", output.ToString());
    }

    private sealed class ClosedWriter : TextWriter
    {
        public int Writes;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { Writes++; throw new IOException("The pipe has been ended."); }
        public override Task WriteAsync(string? value) { Writes++; throw new IOException("The pipe has been ended."); }
    }

    [Fact]
    public async Task StdoutClosed_ExitsCleanly()
    {
        using var f = new Fixture();
        var input = new PipeReader(); // never completes: the server must stop on its own
        input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        input.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var output = new ClosedWriter();
        await f.Server.RunAsync(input, output, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, output.Writes);
    }

    // The real runner, with harmless Windows tools standing in for claude.
    [Fact]
    public async Task ProcessRunner_SendsStdin_CapturesOutput()
    {
        using var home = new TempHome();
        var runner = McpServer.ProcessRunner(new SessionTracker(home.Paths));
        var sort = Path.Combine(Environment.SystemDirectory, "sort.exe");
        var (code, stdout, _) = await runner(new LaunchSpec(sort, [], home.Dir, null), "hello from stdin ü", TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("hello from stdin", stdout);
    }

    [Fact]
    public async Task ProcessRunner_Timeout_KillsAndThrows()
    {
        using var home = new TempHome();
        var runner = McpServer.ProcessRunner(new SessionTracker(home.Paths));
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner(new LaunchSpec(ping, ["-n", "60", "127.0.0.1"], home.Dir, null), "", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] ParseWindows(string cmdLine)
    {
        var p = CommandLineToArgvW(cmdLine, out var argc);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(p); }
    }

    [Fact]
    public async Task Delegate_McpConfigArg_SurvivesWindowsQuoting()
    {
        using var f = new Fixture();
        await CallOne(f, "delegate", new { profile = "work", prompt = "a \"quoted\" prompt with \\ and spaces" });
        var psi = Launcher.StartInfo(f.Runner.Spec!);
        var expected = FixedArgs;
        Assert.Equal(expected, psi.ArgumentList);
        Assert.Equal("", psi.Arguments); // an argument list, never one pre-joined string

        // The command line built from the list (as .NET's PasteArguments does) parses back to the same arguments
        // under the Windows rules (CommandLineToArgvW, the same as the C runtime and Node use).
        var sb = new StringBuilder("claude.exe");
        foreach (var a in psi.ArgumentList) PasteArgument(sb, a);
        var line = sb.ToString();
        Assert.Contains("\"{\\\"mcpServers\\\":{}}\"", line);
        Assert.Equal(expected, ParseWindows(line).Skip(1));
    }

    // A copy of System.PasteArguments.AppendArgument (Windows), which Process.Start uses for ArgumentList; it is
    // internal and takes a ref struct, so it can't be called through reflection.
    private static void PasteArgument(StringBuilder sb, string argument)
    {
        if (sb.Length != 0) sb.Append(' ');
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            sb.Append(argument);
            return;
        }
        sb.Append('"');
        var idx = 0;
        while (idx < argument.Length)
        {
            var c = argument[idx++];
            if (c == '\\')
            {
                var n = 1;
                while (idx < argument.Length && argument[idx] == '\\') { idx++; n++; }
                if (idx == argument.Length) sb.Append('\\', n * 2);
                else if (argument[idx] == '"') { sb.Append('\\', n * 2 + 1).Append('"'); idx++; }
                else sb.Append('\\', n);
                continue;
            }
            if (c == '"') { sb.Append('\\').Append('"'); continue; }
            sb.Append(c);
        }
        sb.Append('"');
    }

    [Fact]
    public async Task Delegate_SameProfile_Refused()
    {
        using var f = new Fixture();
        f.CallerProfile = "work";
        var result = await CallOne(f, "delegate", new { profile = "work", prompt = "x" });
        Assert.True(IsError(result));
        Assert.Contains("work", Text(result));
        Assert.Equal(0, f.Runner.Calls);

        var other = await CallOne(f, "delegate", new { profile = "ci", prompt = "x" });
        Assert.False(IsError(other));
    }

    [Fact]
    public async Task Delegate_Timeout_ReturnsError()
    {
        using var f = new Fixture();
        f.Runner.Answer = _ => throw new TimeoutException();
        var result = await CallOne(f, "delegate", new { profile = "work", prompt = "x" });
        Assert.True(IsError(result));
        Assert.Contains("10 minutes", Text(result));
    }

    [Fact]
    public async Task Delegate_TruncatesOutputTo100KB()
    {
        using var f = new Fixture();
        f.Runner.Answer = _ => (0, new string('a', 300_000), new string('b', 300_000));
        var text = Text(await CallOne(f, "delegate", new { profile = "work", prompt = "x" }));
        Assert.InRange(text.Count(c => c == 'a'), 100_000, 102_500); // plus the few in the notes
        Assert.InRange(text.Count(c => c == 'b'), 100_000, 102_500);
        Assert.Contains("truncated", text);
    }

    // cmd.exe re-parses a .cmd's command line, but the prompt goes on stdin, so nothing user-supplied reaches it.
    [Fact]
    public async Task Delegate_CmdClaude_PromptStaysOffCommandLine()
    {
        using var f = new Fixture(claude: "C:\\npm\\claude.cmd");
        var r = await CallOne(f, "delegate", new { profile = "work", prompt = "x\" & calc & \"" });
        Assert.False(IsError(r));
        Assert.Equal(FixedArgs, f.Runner.Spec!.Args);
        Assert.Equal("x\" & calc & \"", f.Runner.Stdin);
    }

    [Fact]
    public async Task Delegate_NonZeroExit_IsError()
    {
        using var f = new Fixture();
        f.Runner.Answer = _ => (3, "", "boom");
        var result = await CallOne(f, "delegate", new { profile = "work", prompt = "x" });
        Assert.True(IsError(result));
        Assert.Contains("boom", Text(result));
        Assert.Contains("3", Text(result));
    }

    [Fact]
    public async Task UnknownMethod_Error32601()
    {
        using var f = new Fixture();
        var r = Assert.Single(await Talk(f.Server,
            """{"jsonrpc":"2.0","id":7,"method":"nope/never"}""",
            """{"jsonrpc":"2.0","method":"notifications/whatever"}"""));
        Assert.Equal(7, r["id"]!.GetValue<int>());
        Assert.Equal(-32601, r["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedJson_ParseError32700_ServerContinues()
    {
        using var f = new Fixture();
        var rs = await Talk(f.Server, "{not json", "", """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(2, rs.Count);
        Assert.Equal(-32700, rs[0]["error"]!["code"]!.GetValue<int>());
        Assert.Null(rs[0]["id"]);
        Assert.Equal(2, rs[1]["id"]!.GetValue<int>());
        Assert.NotNull(rs[1]["result"]);
    }

    [Fact]
    public async Task UnknownTool_InvalidParams()
    {
        using var f = new Fixture();
        var r = Assert.Single(await Talk(f.Server, Call(4, "rm_rf")));
        Assert.Equal(-32602, r["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Usage_CalledTwiceQuickly_OneFetch()
    {
        using var f = new Fixture();
        var first = Text(await CallOne(f, "usage", new { profile = "work" }));
        f.Now = Start.AddSeconds(30);
        var second = Text(await CallOne(f, "usage", new { profile = "work" }));
        Assert.Equal(1, f.Http.Calls);
        Assert.Contains("42", first);
        Assert.Contains("42", second);

        // A failed fetch isn't retried within 2 minutes either.
        using var g = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await CallOne(g, "usage", new { profile = "work" });
        g.Now = Start.AddSeconds(90);
        await CallOne(g, "usage", new { profile = "work" });
        Assert.Equal(1, g.Http.Calls);
        g.Now = Start.AddMinutes(3);
        await CallOne(g, "usage", new { profile = "work" });
        Assert.Equal(2, g.Http.Calls);
    }

    [Fact]
    public async Task Usage_RateLimited_ServesCacheUntilRetryAfter()
    {
        using var f = new Fixture(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return r;
        });
        new UsageCache(f.Home.Paths).Put(new UsageReading("work", Start.AddMinutes(-30), "max", [new UsageWindow("5h", 77, null)]));

        var first = Text(await CallOne(f, "usage", new { profile = "work" }));
        Assert.Equal(1, f.Http.Calls);
        Assert.Contains("77", first);
        Assert.Contains("rate", first, StringComparison.OrdinalIgnoreCase);

        // Retry-After was 60 s, but the wait is at least 10 minutes.
        f.Now = Start.AddMinutes(5);
        var during = Text(await CallOne(f, "usage", new { profile = "work" }));
        Assert.Equal(1, f.Http.Calls);
        Assert.Contains("77", during);
        Assert.Contains("rate", during, StringComparison.OrdinalIgnoreCase);

        f.Now = Start.AddMinutes(10).AddSeconds(1);
        await CallOne(f, "usage", new { profile = "work" });
        Assert.Equal(2, f.Http.Calls);
    }

    [Fact]
    public async Task Usage_AllProfiles_NotTrackedForToken()
    {
        using var f = new Fixture();
        var text = Text(await CallOne(f, "usage"));
        var profiles = JsonNode.Parse(text)!["profiles"]!.AsArray();
        Assert.Equal(3, profiles.Count);
        Assert.Contains("Usage isn't tracked for Claude token.", text);
    }

    [Fact]
    public async Task Notification_WithError_GetsNoResponse()
    {
        using var f = new Fixture();
        var rs = await Talk(f.Server,
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"rm_rf"}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9}}""");
        Assert.Empty(rs);
    }

    // Every tool, every usage answer: no token, stored token or credential contents in any result or log line.
    [Fact]
    public async Task McpTools_NeverLeakTokens()
    {
        static string Echo(HttpRequestMessage r) =>
            $"{{\"error\":\"bad\",\"auth\":\"{r.Headers.Authorization}\",\"token\":\"{r.Headers.Authorization?.Parameter}\"}}";
        var answers = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    { "five_hour": { "utilization": 42 },
                      "limits": [ { "scope": { "model": { "display_name": "Opus {{r.Headers.Authorization?.Parameter}}" } }, "percent": 71 } ],
                      "token": "{{r.Headers.Authorization?.Parameter}}" }
                    """),
            },
            r => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(Echo(r)) },
            r => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Echo(r)) },
            r => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(Echo(r)) },
            r => throw new HttpRequestException($"reset {r.Headers.Authorization} ({r.Headers.Authorization?.Parameter})"),
        };

        var seen = new StringBuilder();
        foreach (var answer in answers)
        {
            using var f = new Fixture(answer);
            // The child echoes everything it was given, including its environment, on both streams.
            f.Runner.Answer = spec =>
            {
                var all = spec + " " + string.Join(" ", (spec.ExtraEnv ?? new Dictionary<string, string>()).Select(kv => kv.Key + "=" + kv.Value));
                return (0, all, all);
            };
            var home = f.Pm.Config.HomeProfileName!;
            var lines = new List<string>
            {
                """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}""",
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Call(2, "list_profiles"),
                Call(3, "usage"),
                Call(4, "usage", new { profile = "work" }),
                Call(5, "usage", new { profile = home }),
                Call(6, "usage", new { profile = "ci" }),
                Call(7, "set_default", new { profile = "ci" }),
                Call(8, "delegate", new { profile = "ci", prompt = "x" }),
                Call(9, "delegate", new { profile = "work", prompt = "x" }),
                Call(10, "list_profiles"),
            };
            foreach (var r in await Talk(f.Server, [.. lines])) seen.AppendLine(r.ToJsonString());

            // The runner fails with the token in its message.
            f.Runner.Answer = spec => throw new InvalidOperationException("failed with " + spec.ExtraEnv!.GetValueOrDefault("CLAUDE_CODE_OAUTH_TOKEN"));
            foreach (var r in await Talk(f.Server, Call(11, "delegate", new { profile = "ci", prompt = "x" }))) seen.AppendLine(r.ToJsonString());
            f.Runner.Answer = _ => throw new TimeoutException("timed out " + StoredToken);
            foreach (var r in await Talk(f.Server, Call(12, "delegate", new { profile = "ci", prompt = "x" }))) seen.AppendLine(r.ToJsonString());

            // The same profile refused, a missing token, and the log.
            f.CallerProfile = "ci";
            foreach (var r in await Talk(f.Server, Call(13, "delegate", new { profile = "ci", prompt = "x" }))) seen.AppendLine(r.ToJsonString());
            seen.AppendLine(f.Log.ToString());
            Assert.Equal(StoredToken, f.Runner.Spec!.ExtraEnv!["CLAUDE_CODE_OAUTH_TOKEN"]); // it really was injected
        }

        var text = seen.ToString();
        Assert.Contains("42", text);
        Assert.Contains("\"id\":13", text);
        foreach (var canary in new[] { "LEAKCANARY", "HOMECANARY", "TOKNCANARY", "claudeAiOauth", "refresh-" })
            Assert.DoesNotContain(canary, text);
    }

    [Fact]
    public void McpInstall_PrintsCommand_CopiesToClipboard()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        string? copied = null;
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(["mcp", "install"], o, e, pm, _ => 0, _ => 0, clipboard: s => { copied = s; return true; });
        Assert.Equal(0, code);
        var exe = Path.Combine(Path.GetDirectoryName(HelperExePath.Resolve())!, "cr.exe");
        var expected = $"claude mcp add --scope user clauderoo -- \"{exe}\" mcp";
        Assert.Contains(expected, o.ToString());
        Assert.Equal(expected, copied);
        Assert.False(File.Exists(Path.Combine(home.Dir, ".claude.json.bak")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(home.Dir, ".claude.json")));
    }

    [Fact]
    public void McpInstall_ClipboardFails_StillSucceeds()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        var o = new StringWriter();
        var code = Commands.Run(["mcp", "install"], o, new StringWriter(), pm, _ => 0, _ => 0,
            clipboard: _ => throw new InvalidOperationException("no clipboard"));
        Assert.Equal(0, code);
        Assert.Contains("claude mcp add --scope user clauderoo --", o.ToString());
    }

    [Fact]
    public void Mcp_SkipsPreSync_NothingOnStdoutButProtocol()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        pm.Init();
        pm.Add("work");
        Directory.Delete(home.Paths.ProfileDir("work"), recursive: true); // a pre-sync would print notices fixing this
        var o = new StringWriter();
        var e = new StringWriter();
        using var client = new UsageClient(new Handler(_ => throw new InvalidOperationException("no network")), () => Start, "1.0");
        var code = Commands.Run(["mcp"], o, e, pm, _ => 0, _ => 0, usage: client,
            mcpInput: new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n"));
        Assert.Equal(0, code);
        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.NotNull(JsonNode.Parse(line)!["result"]);
        Assert.False(Directory.Exists(home.Paths.ProfileDir("work")));
    }
}
