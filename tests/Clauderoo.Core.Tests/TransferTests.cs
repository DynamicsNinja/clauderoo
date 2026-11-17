using System.Text.Json;
using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class TransferTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Key = "sk-ant-api03-FAKE-KEY-123";
    private const string Token = "sk-ant-oat01-FAKE-TOKEN-456";

    private static (ProfileManager pm, TempHome home, FakeSecretStore keys, FakeSecretStore tokens) Make()
    {
        var home = new TempHome();
        var keys = new FakeSecretStore();
        var tokens = new FakeSecretStore();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, keys, new FakeGcloud(), tokens)
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        return (pm, home, keys, tokens);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    private static string Vertex(ProfileManager pm, string name = "gcp")
    {
        pm.Add(name);
        pm.SetProvider(name, Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "us-east5", SonnetModel = "sonnet-x" });
        pm.SetEnv(name, [new("MY_VAR", "plain-value")], []);
        return name;
    }

    [Fact]
    public void Export_NoSecrets_NoLogin()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.Add("k"); pm.SetProvider("k", Providers.ApiKey, apiKey: Key);
        pm.Add("t"); pm.SetProvider("t", Providers.Token, token: Token);
        // A login file sits in the profile folder; it must not travel either.
        File.WriteAllText(Path.Combine(home.Paths.ProfileDir("t"), ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"me@x.com\"}}");
        var json = pm.Export(null, includeEnv: true);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain("me@x.com", json);
        Assert.DoesNotContain("oauthAccount", json);
        var root = JsonNode.Parse(json)!;
        var profiles = root["profiles"]!.AsArray();
        Assert.Equal("apikey", profiles.First(p => p!["name"]!.GetValue<string>() == "k")!["provider"]!.GetValue<string>());
        Assert.Equal("token", profiles.First(p => p!["name"]!.GetValue<string>() == "t")!["provider"]!.GetValue<string>());
        // The home profile is not exported unless named, and never with a home flag.
        Assert.DoesNotContain(profiles, p => p!["name"]!.GetValue<string>() == "personal");
        Assert.DoesNotContain("\"home\"", json);
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Export_NamedHome_IsLoginProfile()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var root = JsonNode.Parse(pm.Export(["personal"], true))!;
        var p = Assert.Single(root["profiles"]!.AsArray())!;
        Assert.Equal("personal", p["name"]!.GetValue<string>());
        Assert.Equal("login", p["provider"]!.GetValue<string>());
        Assert.DoesNotContain("\"home\"", root.ToJsonString());
    }

    [Fact]
    public void Export_NoEnvFlag_OmitsEnv()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        Vertex(pm);
        Assert.Contains("plain-value", pm.Export(null, includeEnv: true));
        Assert.DoesNotContain("plain-value", pm.Export(null, includeEnv: false));
    }

    [Fact]
    public void Import_CreatesVertexProfileWithEnvAndFolders()
    {
        var (src, srcHome, _, _) = Make(); using var _s = srcHome;
        Vertex(src);
        var dir = Path.Combine(srcHome.Paths.Root, "proj");
        src.Map("gcp", dir);
        var json = src.Export(null, true);

        var (dst, dstHome, _, _) = Make(); using var _d = dstHome;
        var r = dst.Import(json, force: false);
        Assert.Equal(["gcp"], r.Created);
        Assert.Empty(r.Skipped);
        var e = dst.Get("gcp");
        Assert.Equal(Providers.Vertex, e.Provider);
        Assert.Equal("proj-1", e.Vertex!.Project);
        Assert.Equal("sonnet-x", e.Vertex.SonnetModel);
        Assert.Equal("plain-value", e.Env["MY_VAR"]);
        Assert.Equal("gcp", dst.Maps().Single().Profile);
    }

    [Fact]
    public void Import_ExistingWithoutForce_Skipped()
    {
        var (src, srcHome, _, _) = Make(); using var _s = srcHome;
        Vertex(src);
        var json = src.Export(null, true);

        var (dst, dstHome, _, _) = Make(); using var _d = dstHome;
        dst.Add("gcp");
        dst.SetEnv("gcp", [new("KEEP", "me")], []);
        var before = File.ReadAllBytes(dstHome.Paths.ConfigFile);
        var r = dst.Import(json, force: false);
        Assert.Empty(r.Created);
        Assert.Contains(r.Skipped, s => s.StartsWith("gcp"));
        Assert.Equal(before, File.ReadAllBytes(dstHome.Paths.ConfigFile));
    }

    [Fact]
    public void Import_Force_Updates()
    {
        var (src, srcHome, _, _) = Make(); using var _s = srcHome;
        Vertex(src);
        var json = src.Export(null, true);

        var (dst, dstHome, _, _) = Make(); using var _d = dstHome;
        dst.Add("gcp");
        dst.SetEnv("gcp", [new("OLD", "x")], []);
        var r = dst.Import(json, force: true);
        Assert.Equal(["gcp"], r.Updated);
        var e = dst.Get("gcp");
        Assert.Equal(Providers.Vertex, e.Provider);
        Assert.Equal("plain-value", e.Env["MY_VAR"]);
        Assert.False(e.Env.ContainsKey("OLD"));
    }

    [Fact]
    public void Import_ApiKeyProfile_NeedsSecret_UsesLogin()
    {
        var (src, srcHome, _, _) = Make(); using var _s = srcHome;
        src.Add("k"); src.SetProvider("k", Providers.ApiKey, apiKey: Key);
        src.Add("t"); src.SetProvider("t", Providers.Token, token: Token);
        var json = src.Export(null, true);

        var (dst, dstHome, _, _) = Make(); using var _d = dstHome;
        var r = dst.Import(json, false);
        Assert.Equal(["k", "t"], r.Created.OrderBy(x => x));
        Assert.Equal(["k", "t"], r.NeedSecret.Select(s => s.Split(':')[0]).OrderBy(x => x));
        Assert.Contains(r.NeedSecret, s => s.Contains("`cr provider k apikey`"));
        Assert.Contains(r.NeedSecret, s => s.Contains("`cr provider t token`"));
        Assert.Equal(Providers.Login, Providers.Of(dst.Get("k")));
        Assert.Equal(Providers.Login, Providers.Of(dst.Get("t")));
    }

    [Theory]
    [InlineData("apikey")]
    [InlineData("token")]
    public void Import_Force_ExistingSecretProfile_KeepsSecret(string kind)
    {
        var (pm, home, keys, tokens) = Make(); using var _h = home;
        pm.Add("s");
        if (kind == "apikey") pm.SetProvider("s", Providers.ApiKey, apiKey: Key);
        else pm.SetProvider("s", Providers.Token, token: Token);
        var dir = home.Paths.ProfileDir("s");
        foreach (var provider in new[] { "login", "vertex" })
        {
            var json = "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"s\",\"provider\":\"" + provider +
                       "\",\"vertex\":{\"project\":\"p\",\"region\":\"r\"},\"env\":{\"A\":\"1\"}}]}";
            var r = pm.Import(json, force: true);
            Assert.Equal(["s"], r.Updated);
            Assert.Contains(r.KeptProvider!, k => k.StartsWith("s:"));
            Assert.Equal(kind, pm.Get("s").Provider);
            Assert.Equal("1", pm.Get("s").Env["A"]);
        }
        Assert.True((kind == "apikey" ? keys : tokens).Exists(dir));
    }

    [Fact]
    public void Import_OneProfileFails_OthersAndFoldersStillApplied()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        // A file where the profile folder should be makes Add throw for "bad".
        Directory.CreateDirectory(home.Paths.ProfilesDir);
        File.WriteAllText(home.Paths.ProfileDir("bad"), "x");
        var folder = Path.Combine(home.Paths.Root, "f");
        var json = "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"bad\",\"provider\":\"login\"},{\"name\":\"good\",\"provider\":\"login\"}]," +
                   "\"folders\":[{\"dir\":" + JsonSerializer.Serialize(folder) + ",\"profile\":\"good\"}],\"autoSwitch\":{\"t\":1}}";
        var r = pm.Import(json, false);
        Assert.Equal(["good"], r.Created);
        Assert.Contains(r.Skipped, s => s.StartsWith("bad:"));
        Assert.Equal("good", pm.Maps().Single().Profile);
        Assert.True(pm.Config.Extra!.ContainsKey("autoSwitch"));
    }

    [Fact]
    public void Export_FiltersReservedEnv()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.Add("w");
        var cfg = pm.Config;
        cfg.Profiles["w"].Env["anthropic_api_key"] = "sk-hand-edited";
        cfg.Profiles["w"].Env["OK_VAR"] = "fine";
        ConfigStore.Save(home.Paths, cfg);
        var json = pm.Export(null, true);
        Assert.DoesNotContain("sk-hand-edited", json);
        Assert.Contains("OK_VAR", json);
    }

    [Fact]
    public void Import_Force_AbsentOverrides_LeavesOverridesUnchanged()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        pm.Add("o");
        pm.Own("o", "CLAUDE.md");
        pm.Import("{\"schemaVersion\":1,\"profiles\":[{\"name\":\"o\",\"provider\":\"login\"}]}", force: true);
        Assert.Contains("CLAUDE.md", pm.Get("o").Overrides);
    }

    [Fact]
    public void Import_NewerSchema_Refused()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var ex = Assert.Throws<ClauderooException>(() => pm.Import("{\"schemaVersion\":2,\"profiles\":[]}", false));
        Assert.Equal("This export is from a newer clauderoo (schema 2).", ex.Message);
    }

    [Fact]
    public void Import_HomeFlagNeverImported()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var json = "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"extra\",\"home\":true,\"provider\":\"login\"}]}";
        pm.Import(json, false);
        Assert.False(pm.Get("extra").Home);
        Assert.Equal("personal", pm.Config.HomeProfileName);
    }

    [Fact]
    public void Import_InvalidName_Skipped()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var json = "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"Bad Name\",\"provider\":\"login\"},{\"name\":\"ok\",\"provider\":\"login\"}]}";
        var r = pm.Import(json, false);
        Assert.Equal(["ok"], r.Created);
        Assert.Contains(r.Skipped, s => s.StartsWith("Bad Name"));
    }

    [Fact]
    public void Import_AutoSwitch_PassthroughOnlyWhenTargetHasNone()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        var json = "{\"schemaVersion\":1,\"profiles\":[],\"autoSwitch\":{\"threshold\":80}}";
        pm.Import(json, false);
        Assert.Equal(80, pm.Config.Extra!["autoSwitch"].GetProperty("threshold").GetInt32());
        pm.Import("{\"schemaVersion\":1,\"profiles\":[],\"autoSwitch\":{\"threshold\":50}}", false);
        Assert.Equal(80, pm.Config.Extra!["autoSwitch"].GetProperty("threshold").GetInt32());
        // And it is exported back.
        Assert.Equal(80, JsonNode.Parse(pm.Export(null, true))!["autoSwitch"]!["threshold"]!.GetValue<int>());
    }

    [Fact]
    public void Cli_Export_WritesFile_RefusesOverwriteWithoutForce()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        Vertex(pm);
        var file = Path.Combine(home.Paths.Root, "out.json");
        Assert.Equal(0, Run(pm, "export", file).code);
        Assert.True(File.Exists(file));
        var again = Run(pm, "export", file);
        Assert.Equal(1, again.code);
        Assert.Contains("--force", again.e);
        Assert.Equal(0, Run(pm, "export", file, "--force").code);
    }

    [Fact]
    public void Export_WarnsEnvPlainText()
    {
        var (pm, home, _, _) = Make(); using var _h = home;
        Vertex(pm);
        var file = Path.Combine(home.Paths.Root, "w.json");
        var r = Run(pm, "export", file);
        Assert.Contains("plain text", r.e);
        Assert.DoesNotContain("plain text", r.o);
        var file2 = Path.Combine(home.Paths.Root, "w2.json");
        var r2 = Run(pm, "export", file2, "--no-env");
        Assert.DoesNotContain("plain text", r2.e);
        Assert.DoesNotContain("plain-value", File.ReadAllText(file2));
    }

    [Fact]
    public void Cli_Import_RoundTrip_AndSummary()
    {
        var (src, srcHome, _, _) = Make(); using var _s = srcHome;
        Vertex(src);
        src.Add("k"); src.SetProvider("k", Providers.ApiKey, apiKey: Key);
        var file = Path.Combine(srcHome.Paths.Root, "x.json");
        Run(src, "export", file, "gcp", "k");

        var (dst, dstHome, _, _) = Make(); using var _d = dstHome;
        var r = Run(dst, "import", file);
        Assert.Equal(0, r.code);
        Assert.Contains("gcp", r.o);
        Assert.Contains("cr provider k apikey", r.o);
        Assert.Equal(Providers.Vertex, dst.Get("gcp").Provider);
        Assert.Equal(1, Run(dst, "import", Path.Combine(dstHome.Paths.Root, "missing.json")).code);
    }
}
