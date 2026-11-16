using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

/// <summary>Final whole-branch review fixes for the profile providers feature.</summary>
public class ProvidersFinalFixTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);
    private const string Exe = @"C:\x\clauderoo.exe";
    private const string Bak = "settings.json.bak-20261116-100000";

    // ---- Syncer-level helpers ----

    private static (ClauderooConfig cfg, Syncer syncer, TempHome h) SetupSyncer()
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
        var syncer = new Syncer(cfg, h.Paths, () => Now) { HelperExe = Exe };
        return (cfg, syncer, h);
    }

    private static string ProfileSettings(TempHome h) => Path.Combine(h.Paths.ProfileDir("work"), "settings.json");

    private static void Own(ClauderooConfig cfg, TempHome h, string content)
    {
        cfg.Profiles["work"].Overrides.Add("settings.json");
        Directory.CreateDirectory(h.Paths.ProfileDir("work"));
        File.WriteAllText(ProfileSettings(h), content);
    }

    private static void MakeVertex(ProfileEntry e)
    {
        e.Provider = Providers.Vertex;
        e.Vertex = new VertexSettings { Project = "proj", Region = "eu" };
    }

    private static string[] Backups(TempHome h) =>
        Directory.GetFiles(h.Paths.ProfileDir("work"), "settings.json.bak-*");

    // ---- ProfileManager-level helpers ----

    private static (ProfileManager pm, TempHome h) MakePm(ISecretStore? secrets = null)
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, secrets ?? new FakeSecretStore(), new FakeGcloud())
            { HelperExe = Exe };
        pm.Init();
        pm.Add("work");
        return (pm, h);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    // 1
    [Fact]
    public void Owned_FirstOverlay_HandWrittenReservedKeys_BackedUp()
    {
        var (cfg, syncer, h) = SetupSyncer(); using var _ = h;
        var mine = """{"apiKeyHelper":"my-helper","env":{"ANTHROPIC_API_KEY":"sk-mine","USER_VAR":"u"}}""";
        Own(cfg, h, mine);
        MakeVertex(cfg.Profiles["work"]);

        var r = syncer.Sync("work");

        Assert.Empty(r.Problems);
        Assert.Equal(
            $"work: provider settings replaced hand-written provider keys in settings.json (saved {Bak}).",
            Assert.Single(r.Notices));
        Assert.Equal(mine, File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), Bak)));
        var j = JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!.AsObject();
        Assert.False(j.ContainsKey("apiKeyHelper"));
        Assert.False(j["env"]!.AsObject().ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("u", (string?)j["env"]!["USER_VAR"]);
    }

    [Fact]
    public void Owned_AlreadyManaged_NoBackup()
    {
        var (cfg, syncer, h) = SetupSyncer(); using var _ = h;
        Own(cfg, h, """{"a":1}""");
        MakeVertex(cfg.Profiles["work"]);
        syncer.Sync("work");
        // The user hand-edits a managed key afterwards: clauderoo manages this file now, no backup.
        var j = JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!.AsObject();
        j["env"]!["CLOUD_ML_REGION"] = "hand";
        j["apiKeyHelper"] = "x";
        File.WriteAllText(ProfileSettings(h), j.ToJsonString());

        var r = syncer.Sync("work");

        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices);
        Assert.Empty(Backups(h));
        Assert.Equal("eu", (string?)JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!["env"]!["CLOUD_ML_REGION"]);
    }

    [Fact]
    public void Owned_FirstOverlay_NoReservedKeys_NoBackup()
    {
        var (cfg, syncer, h) = SetupSyncer(); using var _ = h;
        Own(cfg, h, """{"a":1,"env":{"USER_VAR":"u"}}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices);
        Assert.Empty(Backups(h));
    }

    [Fact]
    public void Owned_FirstOverlay_ReservedKeysAlreadyMatch_NoBackup()
    {
        var (cfg, syncer, h) = SetupSyncer(); using var _ = h;
        Own(cfg, h, """{"env":{"CLAUDE_CODE_USE_VERTEX":"1"}}""");
        MakeVertex(cfg.Profiles["work"]);
        var r = syncer.Sync("work");
        Assert.Empty(r.Notices);
        Assert.Empty(Backups(h));
    }

    // 2
    [Fact]
    public void Config_UnknownFields_RoundTrip()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(h.Paths.ConfigFile, """
            {
              "defaultProfile": "personal",
              "homeDir": "C:\\h",
              "homeJson": "C:\\h.json",
              "futureTop": {"x": [1, 2]},
              "profiles": {
                "personal": {"home": true, "futureProfileField": "keep-me"}
              }
            }
            """);

        var cfg = ConfigStore.Load(h.Paths)!;
        cfg.DefaultProfile = "personal";
        ConfigStore.Save(h.Paths, cfg);

        var j = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        Assert.Equal(2, (int)j["futureTop"]!["x"]![1]!);
        Assert.Equal("keep-me", (string?)j["profiles"]!["personal"]!["futureProfileField"]);
        Assert.True((bool)j["profiles"]!["personal"]!["home"]!);
        Assert.False(j.ContainsKey("extra"));
        Assert.False(j.ContainsKey("Extra"));
    }

    // 3
    [Fact]
    public void Share_File_NoticeSaysNowSharing()
    {
        var (pm, h) = MakePm(); using var _ = h;
        File.WriteAllText(Path.Combine(h.Paths.DefaultHomeDir, "CLAUDE.md"), "home");
        pm.Own("work", "CLAUDE.md");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "CLAUDE.md"), "mine");

        var r = pm.Share("work", "CLAUDE.md");

        Assert.Equal(
            "work: now sharing CLAUDE.md from personal; previous copy saved as CLAUDE.md.bak-20261116-100000.",
            Assert.Single(r.Notices));
        Assert.DoesNotContain(r.Notices, n => n.Contains("clauderoo own"));
        Assert.Equal("home", File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), "CLAUDE.md")));
    }

    [Fact]
    public void Share_Dir_NoticeSaysNowSharing()
    {
        var (pm, h) = MakePm(); using var _ = h;
        Directory.CreateDirectory(Path.Combine(h.Paths.DefaultHomeDir, "skills"));
        pm.Own("work", "skills");
        var r = pm.Share("work", "skills");
        Assert.Equal(
            "work: now sharing skills from personal; previous copy saved as skills.bak-20261116-100000.",
            Assert.Single(r.Notices));
    }

    [Fact]
    public void Sync_AfterShare_OtherEditsStillSuggestOwn()
    {
        var (pm, h) = MakePm(); using var _ = h;
        File.WriteAllText(Path.Combine(h.Paths.DefaultHomeDir, "CLAUDE.md"), "home");
        pm.SyncAll();
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "CLAUDE.md"), "edited");
        var r = pm.SyncAll();
        Assert.Contains("clauderoo own work CLAUDE.md", Assert.Single(r.Notices));
    }

    // 4
    [Fact]
    public void Remove_MoveFails_KeyKept()
    {
        var (pm, h) = MakePm(new DpapiSecretStore()); using var _ = h;
        pm.SetProvider("work", Providers.ApiKey, null, "sk-keep");
        var locked = Path.Combine(h.Paths.ProfileDir("work"), "busy.txt");
        File.WriteAllText(locked, "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ClauderooException>(() => pm.Remove("work"));

        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), DpapiSecretStore.FileName)));
        Assert.Equal("sk-keep", pm.GetApiKey("work"));
    }

    [Fact]
    public void Remove_ApiKeyGoneAfterRemove()
    {
        var (pm, h) = MakePm(new DpapiSecretStore()); using var _ = h;
        pm.SetProvider("work", Providers.ApiKey, null, "sk-gone");
        pm.Remove("work");

        var moved = Path.Combine(h.Paths.RemovedDir, "work-20261116-100000");
        Assert.True(Directory.Exists(moved));
        Assert.False(File.Exists(Path.Combine(moved, DpapiSecretStore.FileName)));
        Assert.False(Directory.Exists(h.Paths.ProfileDir("work")));
    }

    // 5
    [Fact]
    public void Sync_Mcp_DuplicateKeyHomeJson_Problem()
    {
        var (pm, h) = MakePm(); using var _ = h;
        File.WriteAllText(h.Paths.DefaultHomeJson, """{"mcpServers":{"a":{"command":"x"},"a":{"command":"y"}}}""");
        var r = pm.SyncAll();
        Assert.Contains("work: home .claude.json is not valid JSON; MCP servers not synced", r.Problems);

        File.WriteAllText(h.Paths.DefaultHomeJson, """{"mcpServers":{},"mcpServers":{}}""");
        r = pm.SyncAll();
        Assert.Contains("work: home .claude.json is not valid JSON; MCP servers not synced", r.Problems);
    }

    [Fact]
    public void Sync_Mcp_DuplicateKeyProfileJson_Problem()
    {
        var (pm, h) = MakePm(); using var _ = h;
        File.WriteAllText(h.Paths.DefaultHomeJson, """{"mcpServers":{"a":{"command":"x"}}}""");
        var profileJson = Path.Combine(h.Paths.ProfileDir("work"), ".claude.json");
        const string dup = """{"mcpServers":{"b":1,"b":2}}""";
        File.WriteAllText(profileJson, dup);
        var r = pm.SyncAll();
        Assert.Contains("work: .claude.json is not valid JSON; MCP servers not synced", r.Problems);
        Assert.Equal(dup, File.ReadAllText(profileJson));
    }

    [Fact]
    public void SetEnv_DuplicateKeyHomeJson_DoesNotThrow()
    {
        var (pm, h) = MakePm(); using var _ = h;
        File.WriteAllText(h.Paths.DefaultHomeJson, """{"mcpServers":{"a":1,"a":2}}""");
        var r = pm.SetEnv("work", [new("MY_VAR", "1")], []);
        Assert.NotEmpty(r.Problems);
        Assert.Equal("1", pm.GetEnv("work")["MY_VAR"]);
    }

    // 7
    [Fact]
    public void SetDefault_UsesManagerHelperExe()
    {
        var (pm, h) = MakePm(); using var _ = h;
        pm.SetProvider("work", Providers.ApiKey, null, "sk");
        File.Delete(ProfileSettings(h));
        pm.SetDefault("work");
        var j = JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!;
        Assert.Equal($"\"{Exe}\" apikey work", (string?)j["apiKeyHelper"]);
    }

    // 8
    [Fact]
    public void GetApiKey_UnknownOrHome_Throws()
    {
        var (pm, h) = MakePm(); using var _ = h;
        var ex = Assert.Throws<ClauderooException>(() => pm.GetApiKey("nope"));
        Assert.StartsWith("Unknown profile 'nope'.", ex.Message);
        ex = Assert.Throws<ClauderooException>(() => pm.GetApiKey("personal"));
        Assert.Equal("The home profile always uses its own Claude login.", ex.Message);
        Assert.False(pm.HasApiKey("nope"));
        Assert.False(pm.HasApiKey("personal"));
    }

    [Fact]
    public void Cli_Apikey_UnknownProfile_Exit1_StderrOnly()
    {
        var (pm, h) = MakePm(); using var _ = h;
        var r = Run(pm, "apikey", "nope");
        Assert.Equal(1, r.code);
        Assert.Equal("", r.o);
        Assert.StartsWith("Unknown profile 'nope'.", r.e);
    }
}
