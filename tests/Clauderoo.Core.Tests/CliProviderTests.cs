using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliProviderTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome home, FakeSecretStore secrets, FakeGcloud gcloud) Make()
    {
        var home = new TempHome();
        var secrets = new FakeSecretStore();
        var gcloud = new FakeGcloud();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, secrets, gcloud) { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home, secrets, gcloud);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, Func<string?>? readSecret, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd", readSecret: readSecret);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Provider_Vertex_Args()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, null, "provider", "work", "vertex", "--project", "p1", "--region", "eu", "--sonnet", "s-m", "--haiku", "h-m");
        Assert.Equal(0, r.code);
        var v = pm.Get("work").Vertex!;
        Assert.Equal("vertex", pm.Get("work").Provider);
        Assert.Equal("p1", v.Project);
        Assert.Equal("eu", v.Region);
        Assert.Equal("s-m", v.SonnetModel);
        Assert.Null(v.OpusModel);
        Assert.Equal("h-m", v.HaikuModel);
    }

    [Fact]
    public void Provider_Vertex_MissingRegion_Exit1()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, null, "provider", "work", "vertex", "--project", "p1");
        Assert.Equal(1, r.code);
        Assert.Contains("Vertex needs a project and a region.", r.e);
    }

    [Fact]
    public void Provider_ApiKey_ReadsSecret()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, () => "sk-test-123", "provider", "work", "apikey");
        Assert.Equal(0, r.code);
        Assert.Equal("apikey", pm.Get("work").Provider);
        Assert.Equal("sk-test-123", pm.GetApiKey("work"));
        Assert.DoesNotContain("sk-test-123", r.o + r.e);
    }

    [Fact]
    public void Provider_Home_Exit1()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, null, "provider", "personal", "login");
        Assert.Equal(1, r.code);
        Assert.Contains("The home profile always uses its own Claude login.", r.e);
    }

    [Fact]
    public void Env_SetListUnset()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Equal(0, Run(pm, null, "env", "work", "set", "A=1", "B=x=y").code);
        var list = Run(pm, null, "env", "work");
        Assert.Equal(0, list.code);
        Assert.Contains("A=1", list.o);
        Assert.Contains("B=x=y", list.o);
        Assert.Equal(0, Run(pm, null, "env", "work", "unset", "A").code);
        var after = Run(pm, null, "env", "work");
        Assert.DoesNotContain("A=1", after.o);
        Assert.Contains("B=x=y", after.o);
    }

    [Fact]
    public void Check_Exit1WhenNotOk()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, null, "check", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("Not logged in", r.o);
    }

    [Fact]
    public void Apikey_PrintsOnlyKey()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.ApiKey, null, "sk-secret");
        var r = Run(pm, null, "apikey", "work");
        Assert.Equal(0, r.code);
        Assert.Equal("sk-secret" + Environment.NewLine, r.o);
        Assert.Equal("", r.e);
    }

    [Fact]
    public void Apikey_Missing_Exit1()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = Run(pm, null, "apikey", "work");
        Assert.Equal(1, r.code);
        Assert.Equal("", r.o);
        Assert.NotEqual("", r.e);
    }

    [Fact]
    public void List_ShowsProvider()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "my-gcp-project-123", Region = "eu" });
        pm.Add("k");
        pm.SetProvider("k", Providers.ApiKey, null, "sk");
        var r = Run(pm, null, "list");
        Assert.Contains("vertex (my-gcp-project-123)", r.o);
        Assert.Contains("apikey", r.o);
    }
}
