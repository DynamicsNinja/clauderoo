using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class ProviderOverlayTests
{
    private const string Exe = @"C:\x\clauderoo.exe";

    private static ProfileEntry Vertex(string? sonnet = null, string? opus = null, string? haiku = null) => new()
    {
        Provider = Providers.Vertex,
        Vertex = new VertexSettings { Project = "proj", Region = "us-east5", SonnetModel = sonnet, OpusModel = opus, HaikuModel = haiku },
    };

    private static Overlay Empty => new(new Dictionary<string, string>(), new Dictionary<string, string>());

    [Fact]
    public void Build_Login_Empty()
    {
        var o = ProviderOverlay.Build("work", new ProfileEntry(), Exe);
        Assert.Empty(o.Env);
        Assert.Empty(o.Top);
    }

    [Fact]
    public void Build_Vertex_AllKeys()
    {
        var o = ProviderOverlay.Build("work", Vertex("s", "o", "h"), Exe);
        Assert.Equal(6, o.Env.Count);
        Assert.Equal("1", o.Env["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Equal("proj", o.Env["ANTHROPIC_VERTEX_PROJECT_ID"]);
        Assert.Equal("us-east5", o.Env["CLOUD_ML_REGION"]);
        Assert.Equal("s", o.Env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("o", o.Env["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("h", o.Env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Single(o.Top);
        Assert.Equal("gcloud auth application-default login", o.Top["gcpAuthRefresh"]);
    }

    [Fact]
    public void Build_Vertex_OmitsNullModels()
    {
        var o = ProviderOverlay.Build("work", Vertex(), Exe);
        Assert.Equal(3, o.Env.Count);
        Assert.DoesNotContain("ANTHROPIC_DEFAULT_SONNET_MODEL", o.Env.Keys);
    }

    [Fact]
    public void Build_ApiKey_Helper()
    {
        var o = ProviderOverlay.Build("api", new ProfileEntry { Provider = Providers.ApiKey }, Exe);
        Assert.Empty(o.Env);
        Assert.Equal("\"" + Exe + "\" apikey api", o.Top["apiKeyHelper"]);
    }

    [Fact]
    public void Build_UserEnvIncluded_ProviderWins()
    {
        var e = Vertex();
        e.Env["MY_VAR"] = "1";
        e.Env["CLOUD_ML_REGION"] = "ignored";
        var o = ProviderOverlay.Build("work", e, Exe);
        Assert.Equal("1", o.Env["MY_VAR"]);
        Assert.Equal("us-east5", o.Env["CLOUD_ML_REGION"]);
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("A-B")]
    [InlineData("CLOUD_ML_REGION")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void ValidateEnvName_Rejects(string name) =>
        Assert.Throws<ClauderooException>(() => ProviderOverlay.ValidateEnvName(name));

    [Fact]
    public void ValidateEnvName_Accepts() => ProviderOverlay.ValidateEnvName("MY_VAR");

    [Fact]
    public void Apply_DropsReservedFromHome()
    {
        var s = JsonNode.Parse("""{"apiKeyHelper":"x","gcpAuthRefresh":"y","theme":"dark","env":{"CLAUDE_CODE_USE_VERTEX":"1","ANTHROPIC_AUTH_TOKEN":"t","KEEP":"k"}}""")!.AsObject();
        var r = ProviderOverlay.Apply(s, Empty, [], []);
        Assert.Null(r["apiKeyHelper"]);
        Assert.Null(r["gcpAuthRefresh"]);
        Assert.Equal("dark", (string?)r["theme"]);
        var env = r["env"]!.AsObject();
        Assert.Single(env);
        Assert.Equal("k", (string?)env["KEEP"]);
    }

    [Fact]
    public void Apply_RemovesPreviouslyManaged()
    {
        var s = JsonNode.Parse("""{"customTop":"a","env":{"OLD":"1","KEEP":"k"}}""")!.AsObject();
        var r = ProviderOverlay.Apply(s, Empty, ["OLD"], ["customTop"]);
        Assert.Null(r["customTop"]);
        Assert.Null(r["env"]!["OLD"]);
        Assert.Equal("k", (string?)r["env"]!["KEEP"]);
    }

    [Fact]
    public void Apply_SetsOverlay_AndKeepsUnrelatedKeys()
    {
        var s = JsonNode.Parse("""{"theme":"dark","env":{"KEEP":"k"}}""")!.AsObject();
        var o = new Overlay(new Dictionary<string, string> { ["MY_VAR"] = "v", ["KEEP"] = "new" },
            new Dictionary<string, string> { ["apiKeyHelper"] = "h" });
        var r = ProviderOverlay.Apply(s, o, [], []);
        Assert.Equal("dark", (string?)r["theme"]);
        Assert.Equal("h", (string?)r["apiKeyHelper"]);
        Assert.Equal("v", (string?)r["env"]!["MY_VAR"]);
        Assert.Equal("new", (string?)r["env"]!["KEEP"]);
    }

    [Fact]
    public void Apply_CreatesEnv_WhenMissing()
    {
        var o = new Overlay(new Dictionary<string, string> { ["MY_VAR"] = "v" }, new Dictionary<string, string>());
        var r = ProviderOverlay.Apply(new JsonObject(), o, [], []);
        Assert.Equal("v", (string?)r["env"]!["MY_VAR"]);
    }

    [Fact]
    public void Apply_NoEnvCreated_WhenOverlayEmpty()
    {
        var r = ProviderOverlay.Apply(new JsonObject { ["theme"] = "dark" }, Empty, [], []);
        Assert.False(r.ContainsKey("env"));
    }

    [Fact]
    public void Apply_ExistingEmptiedEnv_IsKept()
    {
        var s = JsonNode.Parse("""{"env":{"CLAUDE_CODE_USE_VERTEX":"1"}}""")!.AsObject();
        var r = ProviderOverlay.Apply(s, Empty, [], []);
        Assert.True(r.ContainsKey("env"));
        Assert.Empty(r["env"]!.AsObject());
    }

    [Fact]
    public void Apply_EnvNotObject_Throws()
    {
        var s = JsonNode.Parse("""{"env":"oops"}""")!.AsObject();
        var ex = Assert.Throws<ClauderooException>(() => ProviderOverlay.Apply(s, Empty, [], []));
        Assert.Equal("settings.json \"env\" is not an object.", ex.Message);
    }

    [Fact]
    public void Apply_DoesNotMutateInput()
    {
        var s = JsonNode.Parse("""{"apiKeyHelper":"x","env":{"CLAUDE_CODE_USE_VERTEX":"1"}}""")!.AsObject();
        var before = s.ToJsonString();
        var o = new Overlay(new Dictionary<string, string> { ["A"] = "1" }, new Dictionary<string, string>());
        ProviderOverlay.Apply(s, o, [], []);
        Assert.Equal(before, s.ToJsonString());
    }

    [Fact]
    public void Config_RoundTrips_ProviderFields()
    {
        using var home = new TempHome();
        var cfg = new ClauderooConfig { Profiles = { ["v"] = Vertex("s") } };
        cfg.Profiles["v"].Env["MY_VAR"] = "1";
        ConfigStore.Save(home.Paths, cfg);
        Assert.Contains("\"provider\"", File.ReadAllText(home.Paths.ConfigFile));
        var l = ConfigStore.Load(home.Paths)!.Profiles["v"];
        Assert.Equal("vertex", l.Provider);
        Assert.Equal("proj", l.Vertex!.Project);
        Assert.Equal("us-east5", l.Vertex.Region);
        Assert.Equal("s", l.Vertex.SonnetModel);
        Assert.Equal("1", l.Env["MY_VAR"]);
    }

    [Fact]
    public void Config_OldFileWithoutProviderFields_Loads()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.Paths.Root);
        File.WriteAllText(home.Paths.ConfigFile, """{"profiles":{"w":{"home":false,"overrides":[]}}}""");
        var e = ConfigStore.Load(home.Paths)!.Profiles["w"];
        Assert.Null(e.Provider);
        Assert.Null(e.Vertex);
        Assert.Empty(e.Env);
        Assert.Equal(Providers.Login, Providers.Of(e));
    }

    [Fact]
    public void Build_SkipsReservedUserEnv_HandEditedConfig()
    {
        var e = new ProfileEntry();
        e.Env["ANTHROPIC_API_KEY"] = "sk-x";
        e.Env["CLAUDE_CODE_USE_VERTEX"] = "1";
        e.Env["MY_VAR"] = "1";
        var o = ProviderOverlay.Build("work", e, Exe);
        Assert.Equal(["MY_VAR"], o.Env.Keys);
    }

    [Theory]
    [InlineData("FOO\n")]
    [InlineData("FOO\r\n")]
    public void ValidateEnvName_RejectsTrailingNewline(string name) =>
        Assert.Throws<ClauderooException>(() => ProviderOverlay.ValidateEnvName(name));

    [Fact]
    public void Build_Vertex_WhitespaceModelsTreatedAsNull()
    {
        var o = ProviderOverlay.Build("work", Vertex("", "  ", "\t"), Exe);
        Assert.Equal(3, o.Env.Count);
        Assert.DoesNotContain("ANTHROPIC_DEFAULT_SONNET_MODEL", o.Env.Keys);
        Assert.DoesNotContain("ANTHROPIC_DEFAULT_OPUS_MODEL", o.Env.Keys);
        Assert.DoesNotContain("ANTHROPIC_DEFAULT_HAIKU_MODEL", o.Env.Keys);
    }

    [Fact]
    public void HelperExePath_InstalledWins_ElseFallback()
    {
        using var home = new TempHome();
        var lad = Path.Combine(home.Dir, "lad");
        const string run = @"C:\run\clauderoo.exe";
        Assert.Equal(run, HelperExePath.Resolve(lad, run));
        var installed = Path.Combine(lad, "clauderoo", "clauderoo.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "");
        Assert.Equal(installed, HelperExePath.Resolve(lad, run));
    }
}
