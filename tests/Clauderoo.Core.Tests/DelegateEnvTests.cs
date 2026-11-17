using System.Net;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

// The real-process test changes this process's environment, so the collection never runs alongside other tests.
[CollectionDefinition(nameof(DelegateEnvTests), DisableParallelization = true)]
public class DelegateEnvCollection;

[Collection(nameof(DelegateEnvTests))]
public class DelegateEnvTests
{
    private static readonly IReadOnlySet<string> NoProfileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Drops_ReservedFixedPrefixedAndProfileKeys_CaseInsensitive()
    {
        foreach (var n in ProviderOverlay.ReservedEnv)
        {
            Assert.True(DelegateEnv.Drops(n, NoProfileKeys), n);
            Assert.True(DelegateEnv.Drops(n.ToLowerInvariant(), NoProfileKeys), n);
        }
        foreach (var n in new[] { "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "ANTHROPIC_MODEL", "anthropic_model",
                     "CLAUDECODE", "ClaudeCode", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "claude_code_anything" })
            Assert.True(DelegateEnv.Drops(n, NoProfileKeys), n);

        var keys = new HashSet<string>(["MY_PROXY"], StringComparer.OrdinalIgnoreCase);
        Assert.True(DelegateEnv.Drops("my_proxy", keys));
        Assert.False(DelegateEnv.Drops("my_proxy", NoProfileKeys));

        foreach (var n in new[] { "PATH", "USERPROFILE", "APPDATA", "SystemRoot", "CLAUDE_CONFIG_DIR", "CLAUDEROO_PROFILE",
                     "HTTPS_PROXY", "ANTHROPIC_X", "CLAUDE_X" })
            Assert.False(DelegateEnv.Drops(n, NoProfileKeys), n);
    }

    [Fact]
    public void Filter_TakesEveryProfilesEnvKeys()
    {
        var cfg = new ClauderooConfig();
        cfg.Profiles["a"] = new ProfileEntry { Env = { ["ONE"] = "1" } };
        cfg.Profiles["b"] = new ProfileEntry { Env = { ["Two"] = "2" } };
        var drop = DelegateEnv.Filter(cfg);
        Assert.True(drop("one"));
        Assert.True(drop("TWO"));
        Assert.False(drop("THREE"));
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // End to end: a delegate's spec, run by the real runner (cmd /c set standing in for claude), shows the child gets
    // none of the caller's provider, model, account or session variables, and does get the target's own.
    [Fact]
    public async Task ProcessRunner_Delegate_CallerEnvNeverReachesChild()
    {
        const string Leak = "CALLERLEAK";
        const string TargetToken = "sk-ant-oat01-TARGETTOKEN-0123456789abcdef";
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => new DateTime(2026, 11, 17));
        pm.Init();
        pm.Add("work");
        pm.Add("ci");
        pm.SetProvider("ci", Providers.Token, token: TargetToken);
        pm.SetEnv("work", [new("MY_PROFILE_SETTING", "v")], []);
        var proj = Directory.CreateDirectory(Path.Combine(home.Dir, "proj")).FullName;

        LaunchSpec? captured = null;
        using var usage = new UsageClient(new NoHttp(), () => DateTimeOffset.UtcNow, "1.0");
        var server = new McpServer(pm, usage, (spec, _, _, _) =>
            {
                captured = spec;
                return Task.FromResult((0, "", ""));
            }, getEnv: _ => null, claudePath: "claude.exe", defaultCwd: proj, log: TextWriter.Null);
        var output = new StringWriter();
        await server.RunAsync(new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"delegate","arguments":{"profile":"ci","prompt":"x"}}}""" + "\n"),
            output, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(captured);

        string[] injected =
        [
            "CLAUDE_CODE_USE_VERTEX", "ANTHROPIC_VERTEX_PROJECT_ID", "CLOUD_ML_REGION", "anthropic_api_key", "ANTHROPIC_AUTH_TOKEN",
            "ANTHROPIC_DEFAULT_SONNET_MODEL", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK",
            "ANTHROPIC_MODEL", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "MY_PROFILE_SETTING",
        ];
        var saved = injected.Append("CLAUDEROO_TEST_KEEP").ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in injected) Environment.SetEnvironmentVariable(n, Leak + "-" + n);
            Environment.SetEnvironmentVariable("CLAUDEROO_TEST_KEEP", "kept");

            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var runner = McpServer.ProcessRunner(new SessionTracker(home.Paths));
            var (code, stdout, _) = await runner(captured! with { FileName = cmd, Args = ["/c", "set"] }, "",
                TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(0, code);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            Assert.DoesNotContain(lines, l => l.Contains(Leak, StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.StartsWith("CLAUDECODE=", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN=" + TargetToken, lines);
            Assert.Contains("CLAUDE_CONFIG_DIR=" + pm.ConfigDirOf("ci"), lines);
            Assert.Contains("CLAUDEROO_PROFILE=ci", lines);
            Assert.Contains("CLAUDEROO_TEST_KEEP=kept", lines); // anything else still passes
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }
    }
}
