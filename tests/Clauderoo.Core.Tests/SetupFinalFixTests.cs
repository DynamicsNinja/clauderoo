using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public sealed class NoHook : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

// Final-review fixes for the Vertex setup check (probe isolation, leftover names, GAC scope, 403, progress,
// shared formatter, Ctrl+C, token never printed).
public class SetupFinalFixTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);
    private const string Token = "ya29.SECRET-TOKEN";

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeGcloud Gcloud = new();
        public FakeMachineEnv Env = new();
        public FakeIam Iam = new();
        public FakeClaude Claude = new();
        public ProfileManager Pm = null!;
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make(VertexSettings? vertex = null)
    {
        var c = new Ctx();
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), c.Gcloud, c.Env, c.Iam, c.Claude)
        { HelperExe = @"C:\x\clauderoo.exe" };
        c.Pm.Init();
        c.Pm.Add("work");
        c.Pm.SetProvider("work", Providers.Vertex, vertex ?? new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return c;
    }

    private static VertexSettings ThreeModels() => new()
    {
        Project = "proj-1", Region = "europe-west1",
        SonnetModel = "claude-sonnet-4-6", OpusModel = "claude-opus-4-8", HaikuModel = "claude-haiku-4-5",
    };

    private sealed class ListProgress : IProgress<CheckItem>
    {
        public List<CheckItem> Items { get; } = [];
        public void Report(CheckItem value) => Items.Add(value);
        // The results only: the planned (Pending) rows and the "testing" (Running) notices left out.
        public List<CheckItem> Results => Items.Where(i => i.Status is not (CheckStatus.Pending or CheckStatus.Running)).ToList();
    }

    // ---------- 1. probe side effects ----------

    [Fact]
    public async Task ModelProbe_Args_IsolateMcpAndSessions()
    {
        using var c = Make(ThreeModels());
        await c.Pm.CheckSetupAsync("work");
        Assert.Equal(3, c.Claude.Calls.Count);
        Assert.All(c.Claude.Calls, x =>
        {
            Assert.Contains("--strict-mcp-config", x.args);
            Assert.Contains("--no-session-persistence", x.args);
            Assert.DoesNotContain("--bare", x.args);
        });
    }

    [Fact]
    public void BuildStartInfo_FreshTempWorkDir_AutoUpdaterOff()
    {
        var psi = ProcessClaudeRunner.BuildStartInfo(@"C:\x\claude.exe", ["-p", "hi"], @"C:\p\work");
        var temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temp, Path.GetFullPath(psi.WorkingDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("clauderoo-probe-", Path.GetFileName(psi.WorkingDirectory));
        Assert.False(Directory.Exists(psi.WorkingDirectory)); // only RunAsync creates (and deletes) it
        Assert.Equal("1", psi.Environment["DISABLE_AUTOUPDATER"]);
        Assert.Equal(@"C:\p\work", psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(["-p", "hi"], psi.ArgumentList);

        var other = ProcessClaudeRunner.BuildStartInfo(@"C:\x\claude.cmd", ["-p"], @"C:\p\work");
        Assert.NotEqual(psi.WorkingDirectory, other.WorkingDirectory); // fresh per run
        Assert.Equal("cmd.exe", other.FileName);
    }

    // ---------- 2. leftover names ----------

    [Theory]
    [InlineData("CLAUDE_CODE_GIT_BASH_PATH", false)]
    [InlineData("CLAUDE_CODE_MAX_OUTPUT_TOKENS", false)]
    [InlineData("CLAUDE_CONFIG_DIR", false)]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", true)]
    [InlineData("CLAUDE_CODE_USE_VERTEX", true)]
    [InlineData("CLAUDE_CODE_SKIP_VERTEX_AUTH", true)]
    [InlineData("claude_code_skip_bedrock_auth", true)]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("CLOUD_ML_REGION", true)]
    [InlineData("VERTEX_REGION_CLAUDE_3_5_SONNET", true)]
    public void IsLeftoverProviderVar_Ruling(string name, bool flagged) =>
        Assert.Equal(flagged, ProfileManager.IsLeftoverProviderVar(name));

    [Fact]
    public async Task Leftovers_IgnoreHarmlessClaudeCodeVars()
    {
        using var c = Make();
        c.Env.UserVars["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\git\bash.exe";
        c.Env.UserVars["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "8000";
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Ok, r[4].Status);
        Assert.Equal("No leftover provider variables", r[4].Label);
    }

    [Fact]
    public async Task Leftovers_FlagProviderAndAuthVars()
    {
        using var c = Make();
        c.Env.UserVars["CLAUDE_CODE_USE_BEDROCK"] = "1";
        c.Env.MachineVars["CLAUDE_CODE_SKIP_VERTEX_AUTH"] = "1";
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Warn, r[4].Status);
        Assert.Equal("Leftover variables: CLAUDE_CODE_USE_BEDROCK (User), CLAUDE_CODE_SKIP_VERTEX_AUTH (Machine)", r[4].Label);
    }

    // ---------- 4. GOOGLE_APPLICATION_CREDENTIALS scope ----------

    [Fact]
    public async Task Gac_UserValueAlsoInProcess_ReportsUser()
    {
        using var c = Make();
        var p = Path.Combine(c.Home.Dir, "gone.json");
        c.Env.UserVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        c.Env.ProcessVars["GOOGLE_APPLICATION_CREDENTIALS"] = p; // inherited copy, not a separate setting
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal($"GOOGLE_APPLICATION_CREDENTIALS points to a missing file ({p}, User)", r[2].Label);
    }

    [Fact]
    public async Task Gac_MachineValueAlsoInProcess_ReportsMachine()
    {
        using var c = Make();
        var p = Path.Combine(c.Home.Dir, "sa.json");
        File.WriteAllText(p, "{}");
        c.Env.MachineVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        c.Env.ProcessVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Warn, r[2].Status);
        Assert.Equal("GOOGLE_APPLICATION_CREDENTIALS is set (Machine) and overrides your gcloud sign-in", r[2].Label);
    }

    [Fact]
    public async Task Gac_UserBeatsMachine()
    {
        using var c = Make();
        var u = Path.Combine(c.Home.Dir, "u.json");
        c.Env.UserVars["GOOGLE_APPLICATION_CREDENTIALS"] = u;
        c.Env.MachineVars["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(c.Home.Dir, "m.json");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal($"GOOGLE_APPLICATION_CREDENTIALS points to a missing file ({u}, User)", r[2].Label);
    }

    [Fact]
    public async Task Gac_ProcessOnly_ReportsProcess()
    {
        using var c = Make();
        var p = Path.Combine(c.Home.Dir, "proc.json");
        c.Env.ProcessVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal($"GOOGLE_APPLICATION_CREDENTIALS points to a missing file ({p}, Process)", r[2].Label);
    }

    // ---------- 5. IAM 403 ----------

    private sealed class StatusHandler(System.Net.HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("") });
    }

    [Fact]
    public async Task Iam_Http403_FailsWithRoleHint()
    {
        using var c = new TempHome();
        var g = new FakeGcloud();
        var pm = new ProfileManager(c.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), g, new FakeMachineEnv(),
            new HttpIamTester(new StatusHandler(System.Net.HttpStatusCode.Forbidden)), new FakeClaude())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        var r = await pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[3].Status);
        Assert.Equal("Vertex AI permission on proj-1", r[3].Label);
        Assert.Equal("Ask your GCP admin for Vertex AI User (roles/aiplatform.user) on proj-1.", r[3].Hint);
    }

    [Fact]
    public async Task Iam_Http500_StillWarns()
    {
        var t = new HttpIamTester(new StatusHandler(System.Net.HttpStatusCode.InternalServerError));
        var (allowed, error) = await t.HasPermissionAsync("p", "tok", "x", default);
        Assert.Null(allowed);
        Assert.Equal("HTTP 500 Internal Server Error", error);
    }

    // ---------- 7. progress ----------

    [Fact]
    public async Task Progress_ReceivesAllItemsInOrder()
    {
        using var c = Make(ThreeModels());
        c.Env.UserVars["ANTHROPIC_MODEL"] = "x";
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p);
        // Results arrive in finish order (checks run in parallel); the same items, one per row.
        Assert.Equal(r.OrderBy(i => i.Key), p.Results.OrderBy(i => i.Key));
        Assert.Equal(9, p.Results.Count);
        Assert.Equal(9 + 9 + 3, p.Items.Count); // plus 9 planned rows and 3 "testing" notices
    }

    [Fact]
    public async Task Progress_EarlyReturnPaths_AllReported()
    {
        using var c = Make();
        c.Gcloud.Path = null;
        var p = new ListProgress();
        var r = await c.Pm.CheckSetupAsync("work", default, p);
        Assert.Equal(r, p.Results);

        using var c2 = Make();
        c2.Claude.Path = null;
        var p2 = new ListProgress();
        var r2 = await c2.Pm.CheckSetupAsync("work", default, p2);
        // The sign-in and IAM results come after the instant checks now (they run in parallel): compare per row.
        Assert.Equal(r2.OrderBy(i => i.Key), p2.Results.OrderBy(i => i.Key));
    }

    [Fact]
    public async Task Progress_ReportsBeforeLaterChecksRun()
    {
        using var c = Make(ThreeModels());
        var p = new ListProgress();
        var seenAtFirstProbe = -1;
        c.Claude.Responder = _ =>
        {
            if (seenAtFirstProbe < 0) seenAtFirstProbe = p.Results.Count;
            return (0, "OK", "");
        };
        await c.Pm.CheckSetupAsync("work", default, p);
        // The instant checks (gcloud, GAC, leftovers, Claude) were already reported; the probes don't wait for the
        // sign-in token, so sign-in and IAM come later.
        Assert.Equal(4, seenAtFirstProbe);
        Assert.Equal(["gcloud", "gac", "leftovers", "claude"], p.Results.Take(4).Select(i => i.Key));
    }

    // ---------- 8. shared formatter ----------

    [Fact]
    public void Formatter_MarksAndSeparator()
    {
        Assert.Equal("\u2713 gcloud installed", CheckItemFormatter.Format(new(CheckStatus.Ok, "gcloud installed", null)));
        Assert.Equal("\u26A0 x  \u2192 do y", CheckItemFormatter.Format(new(CheckStatus.Warn, "x", "do y")));
        Assert.Equal("\u2717 x  \u2192 do y", CheckItemFormatter.Format(new(CheckStatus.Fail, "x", "do y")));
        Assert.Equal("\u2013 x", CheckItemFormatter.Format(new(CheckStatus.Skipped, "x", "")));
        Assert.Equal("\u2717 x  \u2192 do y  (raw)", CheckItemFormatter.Format(new(CheckStatus.Fail, "x", "do y", "raw")));
    }

    // ---------- 6 & 9. CLI ----------

    private static (int code, string o, string e) Cli(ProfileManager pm, Func<CancellationTokenSource, IDisposable> hook,
        params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd", hookCancel: hook);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Check_CtrlC_CancelsPrintsCancelledExit1()
    {
        using var c = Make(ThreeModels());
        CancellationTokenSource? hooked = null;
        var hook = new NoHook();
        c.Claude.Responder = _ =>
        {
            hooked!.Cancel(); // Ctrl+C pressed while the model probes run
            return (0, "OK", "");
        };
        var r = Cli(c.Pm, cts => { hooked = cts; return hook; }, "check", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("Cancelled.", r.e);
        Assert.Contains("\u2713 gcloud installed", r.o); // streamed before the cancel
        Assert.DoesNotContain("answers", r.o); // no model results reported after the cancel
        Assert.Equal(3, c.Claude.Calls.Count); // the probes start together (in parallel)
        Assert.True(hook.Disposed); // Ctrl+C handler removed again
    }

    [Fact]
    public void Cli_Check_PrintsLinesAsTheyArrive()
    {
        using var c = Make();
        var o = new StringWriter();
        string? atProbe = null;
        c.Claude.Responder = _ => { atProbe = o.ToString(); return (0, "OK", ""); };
        var code = Commands.Run(["check", "work"], o, new StringWriter(), c.Pm, _ => 0, _ => 0, wtAvailable: true,
            claudePath: "claude.cmd", hookCancel: _ => new NoHook());
        Assert.Equal(0, code);
        Assert.NotNull(atProbe);
        Assert.Contains("\u2713 Claude Code installed", atProbe);
        Assert.DoesNotContain("Default model answers", atProbe);
        Assert.Contains("\u2713 Default model answers", o.ToString());
    }

    [Fact]
    public void Cli_Check_NeverPrintsToken()
    {
        using var c = Make(ThreeModels());
        c.Gcloud.Result = (0, Token + "\n", "");
        c.Iam.Result = (null, "HTTP 401 for " + Token);
        c.Claude.Responder = args => args.Contains("claude-opus-4-8")
            ? (1, "Authorization: Bearer " + Token + "\n", "error with " + Token)
            : (0, "OK " + Token, "");
        var r = Cli(c.Pm, _ => new NoHook(), "check", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("\u2717 claude-opus-4-8 didn't answer", r.o);
        Assert.DoesNotContain(Token, r.o);
        Assert.DoesNotContain(Token, r.e);
        Assert.DoesNotContain("ya29", r.o + r.e);
    }
}
