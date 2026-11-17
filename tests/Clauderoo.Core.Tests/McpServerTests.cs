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
        public TimeSpan Timeout;
        public int Calls;
        public Func<LaunchSpec, (int, string, string)> Answer = _ => (0, "done", "");

        public Task<(int code, string stdout, string stderr)> Run(LaunchSpec spec, TimeSpan timeout, CancellationToken ct)
        {
            Spec = spec;
            Timeout = timeout;
            Calls++;
            return Task.FromResult(Answer(spec));
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
            Http = new Handler(answer ?? (_ => OkUsage()));
            Usage = new UsageClient(Http, () => Now, "1.0");
            Server = new McpServer(Pm, Usage, Runner.Run, now: () => Now,
                getEnv: n => n == "CLAUDEROO_PROFILE" ? CallerProfile : null,
                claudePath: claude, defaultCwd: Home.Dir, log: Log);
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
    private static async Task<List<JsonObject>> Talk(McpServer s, params string[] lines)
    {
        var output = new StringWriter();
        await s.RunAsync(new StringReader(string.Join("\n", lines) + "\n"), output, CancellationToken.None);
        var outLines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
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
        Assert.Equal(["-p", "say \"hi\" & bye", "--output-format", "text", "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}"""],
            spec.Args);
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
        Assert.Equal(f.Home.Dir, spec.WorkingDir);
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
        string[] expected = ["-p", "a \"quoted\" prompt with \\ and spaces", "--output-format", "text", "--strict-mcp-config",
            "--mcp-config", """{"mcpServers":{}}"""];
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

    // cmd.exe re-parses a .cmd's command line, so quoting can't protect a prompt there: risky prompts are refused.
    [Fact]
    public async Task Delegate_CmdClaude_UnsafePromptRefused()
    {
        using var f = new Fixture(claude: "C:\\npm\\claude.cmd");
        var bad = await CallOne(f, "delegate", new { profile = "work", prompt = "x\" & calc & \"" });
        Assert.True(IsError(bad));
        Assert.Equal(0, f.Runner.Calls);
        var ok = await CallOne(f, "delegate", new { profile = "work", prompt = "summarise the repo, briefly" });
        Assert.False(IsError(ok));
        Assert.Equal(1, f.Runner.Calls);
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
