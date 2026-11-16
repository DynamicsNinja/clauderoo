using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class SyncerOverlayTests
{
    private const string Exe = @"C:\x\clauderoo.exe";
    private const string Bak = "settings.json.bak-20261116-100000";

    private static (ClauderooConfig cfg, Syncer syncer, TempHome h) Setup()
    {
        var h = new TempHome();
        var cfg = new ClauderooConfig
        {
            HomeDir = h.Paths.DefaultHomeDir,
            HomeJson = h.Paths.DefaultHomeJson,
            Profiles =
            {
                ["personal"] = new ProfileEntry { Home = true },
                ["work"] = new ProfileEntry(),
            },
        };
        var syncer = new Syncer(cfg, h.Paths, () => new DateTime(2026, 11, 16, 10, 0, 0)) { HelperExe = Exe };
        return (cfg, syncer, h);
    }

    private static void MakeVertex(ProfileEntry e, string? sonnet = null)
    {
        e.Provider = Providers.Vertex;
        e.Vertex = new VertexSettings { Project = "proj", Region = "eu", SonnetModel = sonnet };
    }

    private static string HomeSettings(ClauderooConfig cfg) => Path.Combine(cfg.HomeDir, "settings.json");
    private static string ProfileSettings(TempHome h) => Path.Combine(h.Paths.ProfileDir("work"), "settings.json");
    private static JsonObject ReadProfile(TempHome h) => JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!.AsObject();

    private static void Own(ClauderooConfig cfg, TempHome h, string content)
    {
        cfg.Profiles["work"].Overrides.Add("settings.json");
        Directory.CreateDirectory(h.Paths.ProfileDir("work"));
        File.WriteAllText(ProfileSettings(h), content);
    }

    private static readonly string[] VertexEnv =
        ["CLAUDE_CODE_USE_VERTEX", "ANTHROPIC_VERTEX_PROJECT_ID", "CLOUD_ML_REGION", "ANTHROPIC_DEFAULT_SONNET_MODEL"];

    // ---- shared ----

    [Fact]
    public void NoProvider_SettingsUnchangedFromToday()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        var home = "{ \"theme\":\"dark\",\n\t\"env\": {\"A\":\"1\"} }\n";
        File.WriteAllText(HomeSettings(cfg), home);
        var r1 = syncer.Sync("work");
        var r2 = syncer.Sync("work");
        Assert.Equal(File.ReadAllBytes(HomeSettings(cfg)), File.ReadAllBytes(ProfileSettings(h)));
        Assert.Empty(r1.Notices); Assert.Empty(r2.Notices);
        Assert.Empty(r1.Problems); Assert.Empty(r2.Problems);
    }

    [Fact]
    public void NoProvider_NonJsonHome_StillByteCopied()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), "not json");
        var r = syncer.Sync("work");
        Assert.Equal("not json", File.ReadAllText(ProfileSettings(h)));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Shared_Vertex_HomeSettingsPlusOverlay()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark","env":{"HOME_VAR":"h","MY_VAR":"home"}}""");
        MakeVertex(cfg.Profiles["work"], sonnet: "claude-sonnet-4-6");
        cfg.Profiles["work"].Env["MY_VAR"] = "mine";
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices);
        var j = ReadProfile(h);
        Assert.Equal("dark", (string?)j["theme"]);
        Assert.Equal("gcloud auth application-default login", (string?)j["gcpAuthRefresh"]);
        var env = j["env"]!.AsObject();
        Assert.Equal("h", (string?)env["HOME_VAR"]);
        Assert.Equal("mine", (string?)env["MY_VAR"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Equal("proj", (string?)env["ANTHROPIC_VERTEX_PROJECT_ID"]);
        Assert.Equal("eu", (string?)env["CLOUD_ML_REGION"]);
        Assert.Equal("claude-sonnet-4-6", (string?)env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Contains("\n  \"theme\": \"dark\"", File.ReadAllText(ProfileSettings(h)).Replace("\r\n", "\n"));

        var st = SyncState.Load(h.Paths.ProfileDir("work"));
        Assert.Equal(VertexEnv.Append("MY_VAR").Order(), st.ManagedEnv.Order());
        Assert.Equal(["gcpAuthRefresh"], st.ManagedTop);
    }

    [Fact]
    public void Shared_Vertex_SecondSync_NoRewriteNoNotice()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        var mtime = File.GetLastWriteTimeUtc(ProfileSettings(h));
        File.SetLastWriteTimeUtc(ProfileSettings(h), mtime.AddMinutes(-5));
        var r = syncer.Sync("work");
        Assert.Empty(r.Notices);
        Assert.Equal(mtime.AddMinutes(-5), File.GetLastWriteTimeUtc(ProfileSettings(h)));
    }

    [Fact]
    public void Shared_LoginProfile_DropsReservedKeysFromHome()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg),
            """{"theme":"dark","apiKeyHelper":"x","gcpAuthRefresh":"y","env":{"CLAUDE_CODE_USE_VERTEX":"1","ANTHROPIC_API_KEY":"k","KEEP":"k"}}""");
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.Equal("dark", (string?)j["theme"]);
        Assert.False(j.ContainsKey("apiKeyHelper"));
        Assert.False(j.ContainsKey("gcpAuthRefresh"));
        var env = j["env"]!.AsObject();
        Assert.False(env.ContainsKey("CLAUDE_CODE_USE_VERTEX"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("k", (string?)env["KEEP"]);
    }

    [Fact]
    public void Shared_SwitchVertexToLogin_RemovesVertexKeys_ThenBackToByteCopy()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        var e = cfg.Profiles["work"];
        MakeVertex(e);
        e.Env["MY_VAR"] = "1";
        syncer.Sync("work");

        e.Provider = null; e.Vertex = null; e.Env.Clear();
        var r1 = syncer.Sync("work");
        var j = ReadProfile(h);
        Assert.Equal("dark", (string?)j["theme"]);
        Assert.False(j.ContainsKey("gcpAuthRefresh"));
        Assert.False(j.ContainsKey("env"));
        var st = SyncState.Load(h.Paths.ProfileDir("work"));
        Assert.Empty(st.ManagedEnv);
        Assert.Empty(st.ManagedTop);

        var r2 = syncer.Sync("work");
        Assert.Equal(File.ReadAllBytes(HomeSettings(cfg)), File.ReadAllBytes(ProfileSettings(h)));
        Assert.Empty(r1.Notices); Assert.Empty(r2.Notices);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.bak-*"));
    }

    [Fact]
    public void Shared_EnableVertexAfterPlainCopy_NoSpuriousBackup()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        syncer.Sync("work");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Notices);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.bak-*"));
        Assert.Equal("1", (string?)ReadProfile(h)["env"]!["CLAUDE_CODE_USE_VERTEX"]);
    }

    [Fact]
    public void Shared_HomeChange_StillFlowsWithOverlay()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        File.WriteAllText(HomeSettings(cfg), """{"theme":"light","model":"opus"}""");
        var r = syncer.Sync("work");
        var j = ReadProfile(h);
        Assert.Equal("light", (string?)j["theme"]);
        Assert.Equal("opus", (string?)j["model"]);
        Assert.Equal("1", (string?)j["env"]!["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Empty(r.Notices);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.bak-*"));
    }

    [Fact]
    public void Shared_SessionEditedFile_BackedUpAndNotified()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        File.WriteAllText(ProfileSettings(h), """{"theme":"edited"}""");
        var r = syncer.Sync("work");
        var dir = h.Paths.ProfileDir("work");
        Assert.Equal("""{"theme":"edited"}""", File.ReadAllText(Path.Combine(dir, Bak)));
        Assert.Equal(
            $"work: your changes to settings.json were replaced by personal's (saved {Bak}). Use 'clauderoo own work settings.json' to keep work's own.",
            Assert.Single(r.Notices));
        var j = ReadProfile(h);
        Assert.Equal("dark", (string?)j["theme"]);
        Assert.Equal("1", (string?)j["env"]!["CLAUDE_CODE_USE_VERTEX"]);
    }

    [Fact]
    public void Shared_HomeMissing_OverlayOntoEmpty()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.Equal("1", (string?)j["env"]!["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Equal(2, j.Count);
    }

    [Fact]
    public void Shared_HomeInvalidJson_ProblemAndNotSynced()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        syncer.Sync("work");
        File.WriteAllText(HomeSettings(cfg), "{oops");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: home settings.json is not valid JSON; settings not synced", Assert.Single(r.Problems));
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(ProfileSettings(h)));
        Assert.Empty(SyncState.Load(h.Paths.ProfileDir("work")).ManagedEnv);
    }

    [Fact]
    public void Shared_HomeEnvNotObject_ProblemAndNotSynced()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"env":"oops"}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: home settings.json \"env\" is not an object; settings not synced", Assert.Single(r.Problems));
        Assert.False(File.Exists(ProfileSettings(h)));
    }

    [Fact]
    public void ApiKey_HelperUsesHelperExe()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), "{}");
        cfg.Profiles["work"].Provider = Providers.ApiKey;
        syncer.Sync("work");
        Assert.Equal("\"" + Exe + "\" apikey work", (string?)ReadProfile(h)["apiKeyHelper"]);
    }

    [Fact]
    public void HelperExe_DefaultsToResolvedPath()
    {
        var (cfg, _, h) = Setup(); using var _ = h;
        var s = new Syncer(cfg, h.Paths, () => DateTime.Now);
        Assert.Equal("clauderoo.exe", Path.GetFileName(s.HelperExe), ignoreCase: true);
    }

    [Fact]
    public void NoProvider_DuplicateKeyHome_StillByteCopied()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"a":1,"a":2,"env":{"X":"1","X":"2"}}""");
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        Assert.Equal(File.ReadAllBytes(HomeSettings(cfg)), File.ReadAllBytes(ProfileSettings(h)));
    }

    [Fact]
    public void Shared_Vertex_DuplicateKeyHome_ProblemNotValidJson()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"env":{"X":"1","X":"2"}}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: home settings.json is not valid JSON; settings not synced", Assert.Single(r.Problems));
        Assert.False(File.Exists(ProfileSettings(h)));
    }

    [Fact]
    public void Owned_DuplicateKey_ProblemUntouched()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        const string dup = """{"a":1,"a":2}""";
        Own(cfg, h, dup);
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: settings.json is not valid JSON; provider settings not applied", Assert.Single(r.Problems));
        Assert.Equal(dup, File.ReadAllText(ProfileSettings(h)));
    }

    [Fact]
    public void SyncAll_DuplicateKey_OtherProfilesStillSyncAndStateSaved()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        Own(cfg, h, """{"a":1,"a":2}""");
        MakeVertex(cfg.Profiles["work"]);
        cfg.Profiles["zzz"] = new ProfileEntry();
        var r = syncer.SyncAll();
        Assert.Single(r.Problems);
        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), SyncState.FileName)));
        Assert.Equal("""{"theme":"dark"}""", File.ReadAllText(Path.Combine(h.Paths.ProfileDir("zzz"), "settings.json")));
    }

    [Fact]
    public void Shared_LoginProfile_BomHome_DropsReservedKeys()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark","apiKeyHelper":"x","env":{"CLOUD_ML_REGION":"r"}}""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(HomeSettings(cfg))[0]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.Equal("dark", (string?)j["theme"]);
        Assert.False(j.ContainsKey("apiKeyHelper"));
        Assert.False(j["env"]!.AsObject().ContainsKey("CLOUD_ML_REGION"));
    }

    [Fact]
    public void OtherSharedFiles_StillByteCopiedWithProvider()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(Path.Combine(cfg.HomeDir, "keybindings.json"), "{ \"k\" : 1 }");
        File.WriteAllText(Path.Combine(cfg.HomeDir, "CLAUDE.md"), "md");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        var dir = h.Paths.ProfileDir("work");
        Assert.Equal("{ \"k\" : 1 }", File.ReadAllText(Path.Combine(dir, "keybindings.json")));
        Assert.Equal("md", File.ReadAllText(Path.Combine(dir, "CLAUDE.md")));
    }

    // ---- owned ----

    [Fact]
    public void Owned_NoProvider_Untouched()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark"}""");
        const string mine = "{\"apiKeyHelper\":\"mine\",  \"x\":1}";
        Own(cfg, h, mine);
        var r = syncer.Sync("work");
        Assert.Equal(mine, File.ReadAllText(ProfileSettings(h)));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Owned_PreservesUnmanagedKeys()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"home"}""");
        Own(cfg, h, """{"permissions":{"allow":["Bash(ls)"]},"statusLine":{"type":"command","command":"x"},"env":{"USER_VAR":"u"}}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices);
        var j = ReadProfile(h);
        Assert.Equal("Bash(ls)", (string?)j["permissions"]!["allow"]![0]);
        Assert.Equal("x", (string?)j["statusLine"]!["command"]);
        Assert.False(j.ContainsKey("theme"));
        Assert.Equal("u", (string?)j["env"]!["USER_VAR"]);
        Assert.Equal("1", (string?)j["env"]!["CLAUDE_CODE_USE_VERTEX"]);
        Assert.Equal("gcloud auth application-default login", (string?)j["gcpAuthRefresh"]);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.bak-*"));
    }

    [Fact]
    public void Owned_SwitchToLogin_RemovesManagedKeys()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Own(cfg, h, """{"permissions":{"allow":[]},"env":{"USER_VAR":"u"}}""");
        var e = cfg.Profiles["work"];
        MakeVertex(e, sonnet: "s");
        e.Env["MY_VAR"] = "1";
        syncer.Sync("work");
        Assert.Equal("1", (string?)ReadProfile(h)["env"]!["MY_VAR"]);

        e.Provider = Providers.Login; e.Vertex = null; e.Env.Clear();
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.NotNull(j["permissions"]);
        Assert.False(j.ContainsKey("gcpAuthRefresh"));
        var env = j["env"]!.AsObject();
        Assert.Equal("u", (string?)env["USER_VAR"]);
        Assert.Single(env);
        var st = SyncState.Load(h.Paths.ProfileDir("work"));
        Assert.Empty(st.ManagedEnv);
        Assert.Empty(st.ManagedTop);
    }

    [Fact]
    public void Owned_Missing_CreatedWithOverlay()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(HomeSettings(cfg), """{"theme":"home"}""");
        cfg.Profiles["work"].Overrides.Add("settings.json");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.False(j.ContainsKey("theme"));
        Assert.Equal("proj", (string?)j["env"]!["ANTHROPIC_VERTEX_PROJECT_ID"]);
    }

    [Fact]
    public void Owned_Invalid_ProblemUntouched()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Own(cfg, h, "{\"oops\"");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: settings.json is not valid JSON; provider settings not applied", Assert.Single(r.Problems));
        Assert.Equal("{\"oops\"", File.ReadAllText(ProfileSettings(h)));
        Assert.Empty(SyncState.Load(h.Paths.ProfileDir("work")).ManagedEnv);
    }

    [Fact]
    public void Owned_EnvNotObject_ProblemUntouched()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Own(cfg, h, """{"env":[1]}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Equal("work: settings.json \"env\" is not an object; provider settings not applied", Assert.Single(r.Problems));
        Assert.Equal("""{"env":[1]}""", File.ReadAllText(ProfileSettings(h)));
    }

    [Fact]
    public void Owned_Unchanged_NotRewritten()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Own(cfg, h, "{}");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        var t = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(ProfileSettings(h), t);
        syncer.Sync("work");
        Assert.Equal(t, File.GetLastWriteTimeUtc(ProfileSettings(h)));
    }

    [Fact]
    public void Owned_SessionWritesDuringMerge_ReMergesWithoutLosingIt()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Own(cfg, h, """{"a":1}""");
        MakeVertex(cfg.Profiles["work"]);
        var fired = false;
        syncer.BeforeOwnedSettingsCommit = () =>
        {
            if (fired) return;
            fired = true;
            File.WriteAllText(ProfileSettings(h), """{"a":1,"b":2,"padding":"changes the length"}""");
        };
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        var j = ReadProfile(h);
        Assert.Equal(2, (int?)j["b"]);
        Assert.Equal("1", (string?)j["env"]!["CLAUDE_CODE_USE_VERTEX"]);
    }

    private static Dictionary<string, (byte[] bytes, DateTime mtime)> Snapshot(ClauderooConfig cfg)
    {
        var d = new Dictionary<string, (byte[], DateTime)>();
        foreach (var f in Directory.GetFiles(cfg.HomeDir, "*", SearchOption.AllDirectories).Append(cfg.HomeJson))
            d[f] = (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f));
        return d;
    }

    [Fact]
    public void NeverWritesHome()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(cfg.HomeJson, "{\"mcpServers\":{\"a\":{\"command\":\"x\"}}}");
        File.WriteAllText(HomeSettings(cfg), """{"theme":"dark","apiKeyHelper":"x","env":{"CLOUD_ML_REGION":"r"}}""");
        File.WriteAllText(Path.Combine(cfg.HomeDir, "CLAUDE.md"), "md");
        MakeVertex(cfg.Profiles["work"]);
        cfg.Profiles["work"].Env["MY_VAR"] = "1";
        cfg.Profiles["api"] = new ProfileEntry { Provider = Providers.ApiKey, Overrides = ["settings.json"] };
        var before = Snapshot(cfg);
        syncer.SyncAll();
        File.WriteAllText(ProfileSettings(h), "X");
        cfg.Profiles["work"].Provider = null;
        syncer.SyncAll();
        var after = Snapshot(cfg);
        Assert.Equal(before.Count, after.Count);
        foreach (var (k, v) in before)
        {
            Assert.Equal(v.bytes, after[k].bytes);
            Assert.Equal(v.mtime, after[k].mtime);
        }
    }
}
