using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public sealed class FakeGcloud : IGcloud
{
    public string? Path { get; set; } = @"C:\fake\gcloud.cmd";
    public (int exit, string stdout, string stderr) Result { get; set; } = (0, "ya29.SECRET-TOKEN\n", "");
    public List<string[]> Calls { get; } = [];
    /// <summary>Runs (on the caller's thread) before <see cref="Result"/> is returned, e.g. to block the token call
    /// until a test condition is met. The setup check calls gcloud off the checking thread.</summary>
    public Func<(int exit, string stdout, string stderr)>? Responder { get; set; }
    public string? Find() => Path;
    public (int exit, string stdout, string stderr) Run(params string[] args)
    {
        lock (Calls) Calls.Add(args);
        return Responder?.Invoke() ?? Result;
    }
}

public class ProfileProvidersTests
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

    private static VertexSettings V(string p = "proj", string r = "eu") => new() { Project = p, Region = r };

    [Fact]
    public void SetProvider_Home_Throws()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var e = Assert.Throws<ClauderooException>(() => pm.SetProvider("personal", Providers.Vertex, V()));
        Assert.Equal("The home profile always uses its own Claude login.", e.Message);
    }

    [Theory]
    [InlineData("", "eu")]
    [InlineData("proj", " ")]
    public void SetProvider_Vertex_RequiresProjectAndRegion(string project, string region)
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var e = Assert.Throws<ClauderooException>(() => pm.SetProvider("work", Providers.Vertex, V(project, region)));
        Assert.Equal("Vertex needs a project and a region.", e.Message);
        Assert.Null(pm.Get("work").Provider);
        Assert.Null(pm.Get("work").Vertex);
    }

    [Fact]
    public void SetProvider_Vertex_SavesAndSyncs()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V());
        Assert.Equal(Providers.Vertex, pm.Get("work").Provider);
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json")))!.AsObject();
        Assert.Equal("1", s["env"]!["CLAUDE_CODE_USE_VERTEX"]!.GetValue<string>());
    }

    [Fact]
    public void SetProvider_ApiKey_StoresSecret()
    {
        var (pm, h, secrets, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-1");
        Assert.Equal("sk-1", pm.GetApiKey("work"));
        Assert.True(pm.HasApiKey("work"));
        Assert.Equal("sk-1", secrets.Load(h.Paths.ProfileDir("work")));
        // existing key is kept when none supplied
        pm.SetProvider("work", Providers.ApiKey);
        Assert.Equal("sk-1", pm.GetApiKey("work"));
    }

    [Fact]
    public void SetProvider_ApiKey_WithoutKey_Message()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var e = Assert.Throws<ClauderooException>(() => pm.SetProvider("work", Providers.ApiKey));
        Assert.Equal("Enter an API key.", e.Message);
    }

    [Fact]
    public void SetProvider_LeaveApiKey_DeletesSecret()
    {
        var (pm, h, secrets, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-1");
        pm.SetProvider("work", Providers.Login);
        Assert.False(secrets.Exists(h.Paths.ProfileDir("work")));
        Assert.False(pm.HasApiKey("work"));
    }

    [Fact]
    public void SetEnv_Rejects_InvalidAndReserved()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.SetEnv("work", [new("1BAD", "x")], []));
        Assert.Throws<ClauderooException>(() => pm.SetEnv("work", [new("ANTHROPIC_API_KEY", "x")], []));
        Assert.Empty(pm.GetEnv("work"));
    }

    [Fact]
    public void SetEnv_Home_Throws()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var e = Assert.Throws<ClauderooException>(() => pm.SetEnv("personal", [new("A", "1")], []));
        Assert.Equal("The home profile always uses its own Claude login.", e.Message);
    }

    [Fact]
    public void SetEnv_SetUnset_Syncs()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetEnv("work", [new("A", "1"), new("B", "2")], []);
        pm.SetEnv("work", [new("A", "3")], ["B"]);
        var env = pm.GetEnv("work");
        Assert.Equal("3", env["A"]);
        Assert.False(env.ContainsKey("B"));
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json")))!.AsObject();
        Assert.Equal("3", s["env"]!["A"]!.GetValue<string>());
        Assert.Null(s["env"]!["B"]);
    }

    [Fact]
    public void ReplaceEnv_ReplacesAll()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetEnv("work", [new("A", "1")], []);
        pm.ReplaceEnv("work", new Dictionary<string, string> { ["C"] = "9" });
        var env = pm.GetEnv("work");
        Assert.Single(env);
        Assert.Equal("9", env["C"]);
        Assert.Throws<ClauderooException>(() => pm.ReplaceEnv("work", new Dictionary<string, string> { ["bad name"] = "x" }));
        Assert.Equal("9", pm.GetEnv("work")["C"]);
    }

    [Fact]
    public void Check_Vertex_Ok_NeverIncludesToken()
    {
        var (pm, h, _, g) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V());
        var r = pm.Check("work");
        Assert.True(r.Ok);
        Assert.Equal("Google sign-in OK.", r.Message);
        Assert.DoesNotContain("SECRET-TOKEN", r.Message);
        Assert.Equal(["auth", "application-default", "print-access-token"], g.Calls.Single());
    }

    [Fact]
    public void Check_Vertex_NoGcloud()
    {
        var (pm, h, _, g) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V());
        g.Path = null;
        var r = pm.Check("work");
        Assert.False(r.Ok);
        Assert.Equal("gcloud not found — install it with: winget install Google.CloudSDK", r.Message);
    }

    [Fact]
    public void Check_Vertex_Fails_ShowsStderrLine()
    {
        var (pm, h, _, g) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V());
        g.Result = (1, "", "\nReauth needed\nsecond line");
        var r = pm.Check("work");
        Assert.False(r.Ok);
        Assert.Equal("Google sign-in failed: Reauth needed", r.Message);
    }

    [Fact]
    public void Check_Vertex_EmptyToken_Fails()
    {
        var (pm, h, _, g) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V());
        g.Result = (0, "  \n", "");
        Assert.False(pm.Check("work").Ok);
    }

    [Fact]
    public void Check_Login_Email()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var r = pm.Check("work");
        Assert.False(r.Ok);
        Assert.Equal("Not logged in — start a session to sign in.", r.Message);
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), ".claude.json"),
            """{"oauthAccount":{"emailAddress":"a@b.c"}}""");
        r = pm.Check("work");
        Assert.True(r.Ok);
        Assert.Equal("Logged in as a@b.c.", r.Message);
    }

    [Fact]
    public void Check_ApiKey()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        var cfg = pm.Config; // hand-edited state: apikey provider, no key stored
        cfg.Profiles["work"].Provider = Providers.ApiKey;
        ConfigStore.Save(h.Paths, cfg);
        Assert.Equal(new CheckResult(false, "No API key saved."), pm.Check("work"));
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-1");
        Assert.Equal(new CheckResult(true, "Key saved — not verified. Start a session to test it.", false), pm.Check("work"));
    }

    [Fact]
    public void Remove_DeletesApiKey()
    {
        var (pm, h, secrets, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-1");
        pm.Remove("work");
        // Deleted from the moved folder, after the move succeeded.
        var moved = Path.Combine(h.Paths.RemovedDir, "work-20261116-100000");
        Assert.Equal(moved, secrets.Deleted[^1]);
    }

    [Fact]
    public void List_IncludesProvider()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        pm.SetProvider("work", Providers.Vertex, V("my-proj"));
        var list = pm.List();
        var home = list.Single(p => p.Name == "personal");
        var work = list.Single(p => p.Name == "work");
        Assert.Equal(Providers.Login, home.Provider);
        Assert.Equal(Providers.Vertex, work.Provider);
        Assert.Equal("my-proj", work.ProviderDetail);
    }
}
