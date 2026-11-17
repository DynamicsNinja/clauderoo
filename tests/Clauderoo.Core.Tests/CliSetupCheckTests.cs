using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliSetupCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome h, FakeGcloud g, FakeMachineEnv env) Make(bool vertex = true)
    {
        var h = new TempHome();
        var g = new FakeGcloud();
        var env = new FakeMachineEnv();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), g, env, new FakeIam(), new FakeClaude())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        if (vertex)
            pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return (pm, h, g, env);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
            hookCancel: _ => new NoHook());
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Check_Vertex_PrintsLines_Exit0WhenOnlyWarn()
    {
        var (pm, h, _, env) = Make(); using var _h = h;
        env.UserVars["ANTHROPIC_FOO"] = "x";
        var r = Run(pm, "check", "work");
        Assert.Equal(0, r.code);
        Assert.Contains("Testing models (this sends a tiny prompt per model)…", r.o);
        Assert.Contains("✓ gcloud installed", r.o);
        Assert.Contains("⚠ ", r.o);
        Assert.Contains("  → ", r.o);
        Assert.DoesNotContain("✗", r.o);
    }

    [Fact]
    public void Cli_Check_Vertex_Exit1OnFail()
    {
        var (pm, h, g, _) = Make(); using var _h = h;
        g.Path = null;
        var r = Run(pm, "check", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("✗ gcloud installed", r.o);
        Assert.Contains("– ", r.o);
    }

    [Fact]
    public void Cli_Check_Login_UsesSimpleCheck()
    {
        var (pm, h, _, _) = Make(vertex: false); using var _h = h;
        var r = Run(pm, "check", "work");
        Assert.DoesNotContain("Testing models", r.o);
        Assert.DoesNotContain("gcloud installed", r.o);
    }
}
