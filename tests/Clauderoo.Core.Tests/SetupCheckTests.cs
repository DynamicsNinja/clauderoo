namespace Clauderoo.Core.Tests;

public sealed class FakeMachineEnv : IMachineEnv
{
    public Dictionary<string, string> UserVars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MachineVars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProcessVars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> User() => UserVars;
    public IReadOnlyDictionary<string, string> Machine() => MachineVars;
    public string? Process(string name) => ProcessVars.GetValueOrDefault(name);
}

public sealed class FakeIam : IIamTester
{
    public (bool? allowed, string? error) Result { get; set; } = (true, null);
    public Task<(bool? allowed, string? error)> HasPermissionAsync(string project, string token, string permission, CancellationToken ct)
        => Task.FromResult(Result);
}

public sealed class FakeClaude : IClaudeRunner
{
    public string? Path { get; set; } = @"C:\fake\claude.cmd";
    public Func<IReadOnlyList<string>, (int, string, string)> Responder { get; set; } = _ => (0, "OK\n", "");
    public List<(IReadOnlyList<string> args, string configDir)> Calls { get; } = [];
    public string? Find() => Path;
    public Task<(int exit, string stdout, string stderr)> RunAsync(string claudePath, IReadOnlyList<string> args,
        string configDir, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((args, configDir));
        var (e, o, s) = Responder(args);
        return Task.FromResult((e, o, s));
    }
}

public class SetupCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

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

    private static Ctx Make(VertexSettings? vertex = null, bool setVertex = true)
    {
        var c = new Ctx();
        c.Pm = new ProfileManager(c.Home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), c.Gcloud, c.Env, c.Iam, c.Claude)
        { HelperExe = @"C:\x\clauderoo.exe" };
        c.Pm.Init();
        c.Pm.Add("work");
        if (setVertex)
            c.Pm.SetProvider("work", Providers.Vertex, vertex ?? new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return c;
    }

    private static VertexSettings WithModels(string? s = null, string? o = null, string? h = null) =>
        new() { Project = "proj-1", Region = "europe-west1", SonnetModel = s, OpusModel = o, HaikuModel = h };

    [Fact]
    public async Task Order_AllOk_SevenPlusModels()
    {
        using var c = Make(WithModels("claude-sonnet-4-6", "claude-opus-4-8"));
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(8, r.Count);
        Assert.All(r, i => Assert.Equal(CheckStatus.Ok, i.Status));
        Assert.Equal(new[]
        {
            "gcloud installed", "Google sign-in for Claude", "GOOGLE_APPLICATION_CREDENTIALS not set",
            "Vertex AI permission on proj-1", "No leftover provider variables", "Claude Code installed",
            "claude-sonnet-4-6 answers", "claude-opus-4-8 answers",
        }, r.Select(i => i.Label));
    }

    [Fact]
    public async Task Order_GcloudMissing_RestSkipped()
    {
        using var c = Make();
        c.Gcloud.Path = null;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[0].Status);
        Assert.Equal("Install it: winget install Google.CloudSDK, then open a new terminal.", r[0].Hint);
        Assert.Equal(7, r.Count);
        Assert.All(r.Skip(1), i => { Assert.Equal(CheckStatus.Skipped, i.Status); Assert.Null(i.Hint); });
        Assert.Equal("Default model answers", r[^1].Label);
    }

    [Fact]
    public async Task TokenFail_SkipsIamAndModels()
    {
        using var c = Make();
        c.Gcloud.Result = (1, "", "ERROR: not logged in\nmore");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[1].Status);
        Assert.Equal("Google sign-in for Claude: ERROR: not logged in", r[1].Label);
        Assert.Equal(CheckStatus.Skipped, r[3].Status);
        Assert.Equal(CheckStatus.Ok, r[5].Status);
        Assert.Equal(CheckStatus.Skipped, r[6].Status);
        Assert.Empty(c.Claude.Calls);
    }

    [Fact]
    public async Task ClaudeMissing_SkipsModels()
    {
        using var c = Make();
        c.Claude.Path = null;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[5].Status);
        Assert.Equal("Install Claude Code, then open a new terminal.", r[5].Hint);
        Assert.Equal(CheckStatus.Skipped, r[6].Status);
    }

    [Fact]
    public async Task Gac_Unset_Ok()
    {
        using var c = Make();
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Ok, r[2].Status);
    }

    [Fact]
    public async Task Gac_MissingFile_Fail()
    {
        using var c = Make();
        var p = Path.Combine(c.Home.Dir, "nope.json");
        c.Env.UserVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[2].Status);
        Assert.Equal($"GOOGLE_APPLICATION_CREDENTIALS points to a missing file ({p}, User)", r[2].Label);
        Assert.Equal("Delete the variable, then close all terminals.", r[2].Hint);
    }

    [Fact]
    public async Task Gac_ExistingFile_Warn()
    {
        using var c = Make();
        var p = Path.Combine(c.Home.Dir, "sa.json");
        File.WriteAllText(p, "{}");
        c.Env.MachineVars["GOOGLE_APPLICATION_CREDENTIALS"] = p;
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Warn, r[2].Status);
        Assert.Equal("GOOGLE_APPLICATION_CREDENTIALS is set (Machine) and overrides your gcloud sign-in", r[2].Label);
        Assert.Equal("Remove it unless you use a service account on purpose.", r[2].Hint);
    }

    [Fact]
    public async Task Gac_ProcessScope_Detected()
    {
        using var c = Make();
        c.Env.ProcessVars["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(c.Home.Dir, "gone.json");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[2].Status);
        Assert.Contains("Process", r[2].Label);
    }

    [Fact]
    public async Task Leftovers_UserAndMachine_Warn_IgnoresConfigDir()
    {
        using var c = Make();
        c.Env.UserVars["ANTHROPIC_MODEL"] = "x";
        c.Env.UserVars["CLAUDE_CONFIG_DIR"] = "y";
        c.Env.MachineVars["CLOUD_ML_REGION"] = "z";
        c.Env.ProcessVars["VERTEX_REGION_FOO"] = "ignored";
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Warn, r[4].Status);
        Assert.Equal("Leftover variables: ANTHROPIC_MODEL (User), CLOUD_ML_REGION (Machine)", r[4].Label);
        Assert.Equal("Remove them in Windows settings \u203A Environment variables, then restart terminals.", r[4].Hint);
    }

    [Fact]
    public async Task Iam_Denied_Fail()
    {
        using var c = Make();
        c.Iam.Result = (false, null);
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[3].Status);
        Assert.Equal("Ask your GCP admin for Vertex AI User (roles/aiplatform.user) on proj-1.", r[3].Hint);
    }

    [Fact]
    public async Task Iam_Error_Warn()
    {
        using var c = Make();
        c.Iam.Result = (null, "HTTP 503 Service Unavailable");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Warn, r[3].Status);
        Assert.Equal("Could not check Vertex AI permission: HTTP 503 Service Unavailable", r[3].Label);
    }

    [Fact]
    public async Task Iam_Error_NeverContainsToken()
    {
        using var c = Make();
        c.Iam.Result = (null, "boom ya29.SECRET-TOKEN boom");
        c.Claude.Responder = _ => (1, "", "bad ya29.SECRET-TOKEN");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.All(r, i =>
        {
            Assert.DoesNotContain("ya29.SECRET-TOKEN", i.Label);
            Assert.DoesNotContain("ya29.SECRET-TOKEN", i.Hint ?? "");
        });
    }

    [Fact]
    public async Task Models_EachConfigured_OneRun()
    {
        using var c = Make(WithModels("claude-sonnet-4-6", "claude-opus-4-8", "claude-haiku-4-5"));
        await c.Pm.CheckSetupAsync("work");
        Assert.Equal(3, c.Claude.Calls.Count);
        Assert.Contains(c.Claude.Calls, x => x.args.SequenceEqual(["-p", "Reply with exactly: OK", "--model", "claude-opus-4-8"]));
        Assert.All(c.Claude.Calls, x => Assert.Equal(c.Home.Paths.ProfileDir("work"), x.configDir));
    }

    [Fact]
    public async Task Models_NoneConfigured_SingleDefaultRun()
    {
        using var c = Make();
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Single(c.Claude.Calls);
        Assert.DoesNotContain("--model", c.Claude.Calls[0].args);
        Assert.Equal("Default model answers", r[^1].Label);
    }

    [Fact]
    public async Task Model_Timeout_ReportsFail()
    {
        using var c = Make(WithModels(o: "claude-opus-4-8"));
        c.Claude.Responder = _ => (-1, "", "timed out after 60 s");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[^1].Status);
        Assert.Equal("claude-opus-4-8 answers: timed out after 60 s", r[^1].Label);
        Assert.Equal("Check the model is enabled in Model Garden and served in region europe-west1.", r[^1].Hint);
    }

    [Fact]
    public async Task Model_OutputWithoutOk_Fail()
    {
        using var c = Make();
        c.Claude.Responder = _ => (0, "hello there\n", "");
        var r = await c.Pm.CheckSetupAsync("work");
        Assert.Equal(CheckStatus.Fail, r[^1].Status);
        Assert.Equal("Default model answers: hello there", r[^1].Label);
    }

    [Fact]
    public async Task NonVertex_Throws()
    {
        using var c = Make(setVertex: false);
        await Assert.ThrowsAsync<ClauderooException>(() => c.Pm.CheckSetupAsync("work"));
    }
}

public class SetupCheckHelpersTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(f(r));
    }

    private static HttpResponseMessage Json(string body, System.Net.HttpStatusCode code = System.Net.HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public void BuildCmdLine_SpacedPath_WrapsWholeCommand()
    {
        var s = ProcessClaudeRunner.BuildCmdLine(@"C:\Users\First Last\claude.cmd", ["-p", "Reply with exactly: OK", "--model", "m"]);
        Assert.Equal("""/d /s /c ""C:\Users\First Last\claude.cmd" -p "Reply with exactly: OK" --model m" """.TrimEnd(), s);
    }

    [Fact]
    public async Task Iam_PermissionPresent_True()
    {
        var t = new HttpIamTester(new StubHandler(_ => Json("{\"permissions\":[\"aiplatform.endpoints.predict\"]}")));
        Assert.Equal((true, (string?)null), await t.HasPermissionAsync("p", "tok", "aiplatform.endpoints.predict", default));
    }

    [Fact]
    public async Task Iam_PermissionAbsent_False()
    {
        var t = new HttpIamTester(new StubHandler(_ => Json("{}")));
        Assert.Equal((false, (string?)null), await t.HasPermissionAsync("p", "tok", "aiplatform.endpoints.predict", default));
    }

    [Fact]
    public async Task Iam_403_ReportsHttpError()
    {
        var t = new HttpIamTester(new StubHandler(_ => Json("", System.Net.HttpStatusCode.Forbidden)));
        Assert.Equal(((bool?)null, (string?)"HTTP 403 Forbidden"), await t.HasPermissionAsync("p", "tok", "x", default));
    }

    [Fact]
    public async Task Iam_ExceptionWithToken_Scrubbed()
    {
        var t = new HttpIamTester(new StubHandler(_ => throw new HttpRequestException("failed for ya29.SECRET")));
        var (allowed, error) = await t.HasPermissionAsync("p", "ya29.SECRET", "x", default);
        Assert.Null(allowed);
        Assert.NotNull(error);
        Assert.DoesNotContain("ya29.SECRET", error);
    }
}
