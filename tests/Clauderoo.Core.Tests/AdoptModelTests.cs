using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class AdoptModelTests
{
    private const string Exe = @"C:\x\clauderoo.exe";
    private const string Bak = "settings.json.bak-20261117-100000";
    private const string Opus = "ANTHROPIC_DEFAULT_OPUS_MODEL";
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);

    private static (ClauderooConfig cfg, Syncer syncer, TempHome h) Setup(bool vertex = true)
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
        if (vertex)
        {
            cfg.Profiles["work"].Provider = Providers.Vertex;
            cfg.Profiles["work"].Vertex = new VertexSettings
            {
                Project = "proj", Region = "eu", SonnetModel = "claude-sonnet-4-6", OpusModel = "claude-opus-4-1",
            };
        }
        File.WriteAllText(Path.Combine(cfg.HomeDir, "settings.json"), """{"theme":"dark"}""");
        ConfigStore.Save(h.Paths, cfg);
        return (cfg, NewSyncer(cfg, h), h);
    }

    private static Syncer NewSyncer(ClauderooConfig cfg, TempHome h) => new(cfg, h.Paths, () => Now) { HelperExe = Exe };

    private static string Dir(TempHome h) => h.Paths.ProfileDir("work");
    private static string ProfileSettings(TempHome h) => Path.Combine(Dir(h), "settings.json");
    private static JsonObject ReadProfile(TempHome h) => JsonNode.Parse(File.ReadAllText(ProfileSettings(h)))!.AsObject();
    private static string? Env(TempHome h, string key) => (string?)ReadProfile(h)["env"]?[key];
    private static string[] Backups(TempHome h) => Directory.GetFiles(Dir(h), "*.bak-*");

    /// <summary>Simulates a Claude session editing the profile's settings.json.</summary>
    private static void SessionEdit(TempHome h, Action<JsonObject> edit)
    {
        var j = ReadProfile(h);
        edit(j);
        File.WriteAllText(ProfileSettings(h), j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Replaced =>
        $"work: your changes to settings.json were replaced by personal's (saved {Bak}). Use 'clauderoo own work settings.json' to keep work's own.";

    [Fact]
    public void Adopt_Opus_PersistsAcrossSyncs()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]![Opus] = "claude-opus-5-5");

        var r = syncer.Sync("work");
        Assert.Equal("work: Opus model updated to claude-opus-5-5 by Claude.", Assert.Single(r.Notices));
        Assert.Empty(r.Problems);
        Assert.Empty(Backups(h));
        Assert.Equal("claude-opus-5-5", cfg.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-5-5", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-5-5", Env(h, Opus));

        var r2 = syncer.Sync("work");
        Assert.Empty(r2.Notices);
        Assert.Equal("claude-opus-5-5", Env(h, Opus));

        // A fresh load (as the tray/CLI would do next time) keeps it too.
        var fresh = ConfigStore.Load(h.Paths)!;
        var r3 = NewSyncer(fresh, h).Sync("work");
        Assert.Empty(r3.Notices);
        Assert.Equal("claude-opus-5-5", Env(h, Opus));
        Assert.Equal("claude-opus-5-5", fresh.Profiles["work"].Vertex!.OpusModel);
        Assert.Empty(Backups(h));
    }

    [Fact]
    public void ConfigChange_NotOverwrittenByStaleFile()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        // The window saves a new model; the profile file still holds what clauderoo last wrote.
        cfg.Profiles["work"].Vertex!.OpusModel = "claude-opus-6";
        ConfigStore.Save(h.Paths, cfg);

        var r = syncer.Sync("work");
        Assert.Empty(r.Notices);
        Assert.Equal("claude-opus-6", Env(h, Opus));
        Assert.Equal("claude-opus-6", cfg.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-6", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }

    [Fact]
    public void ConfigChange_WithOtherSessionEdit_NotOverwrittenByStaleModel()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["theme"] = "edited"); // session touched something else; model untouched
        cfg.Profiles["work"].Vertex!.OpusModel = "claude-opus-6";
        ConfigStore.Save(h.Paths, cfg);

        var r = syncer.Sync("work");
        Assert.Equal(Replaced, Assert.Single(r.Notices));
        Assert.Equal("claude-opus-6", Env(h, Opus));
        Assert.Equal("claude-opus-6", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }

    [Fact]
    public void ProjectChange_StillRestored()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]!["ANTHROPIC_VERTEX_PROJECT_ID"] = "other");

        var r = syncer.Sync("work");
        Assert.Equal(Replaced, Assert.Single(r.Notices));
        Assert.True(File.Exists(Path.Combine(Dir(h), Bak)));
        Assert.Equal("proj", Env(h, "ANTHROPIC_VERTEX_PROJECT_ID"));
        Assert.Equal("proj", cfg.Profiles["work"].Vertex!.Project);
        Assert.Equal("proj", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.Project);
    }

    [Fact]
    public void RemovedKey_Restored()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]!.AsObject().Remove(Opus));

        var r = syncer.Sync("work");
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Equal("claude-opus-4-1", Env(h, Opus));
        Assert.Equal("claude-opus-4-1", cfg.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-4-1", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }

    [Fact]
    public void ModelPlusOther_AdoptsAndBacksUp()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j =>
        {
            j["env"]![Opus] = "claude-opus-5-5";
            j["theme"] = "edited";
        });

        var r = syncer.Sync("work");
        Assert.Equal(["work: Opus model updated to claude-opus-5-5 by Claude.", Replaced], r.Notices);
        var bak = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir(h), Bak)))!;
        Assert.Equal("edited", (string?)bak["theme"]);
        Assert.Equal("dark", (string?)ReadProfile(h)["theme"]);
        Assert.Equal("claude-opus-5-5", Env(h, Opus));
        Assert.Equal("claude-opus-5-5", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }

    [Fact]
    public void Adopt_Owned_SettingsJson()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        cfg.Profiles["work"].Overrides.Add("settings.json");
        Directory.CreateDirectory(Dir(h));
        File.WriteAllText(ProfileSettings(h), """{"mine":"yes"}""");
        syncer.Sync("work");
        SessionEdit(h, j =>
        {
            j["env"]!["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "claude-haiku-5";
            j["other"] = "kept";
        });

        var r = syncer.Sync("work");
        Assert.Equal("work: Haiku model updated to claude-haiku-5 by Claude.", Assert.Single(r.Notices));
        Assert.Empty(Backups(h));
        var j = ReadProfile(h);
        Assert.Equal("yes", (string?)j["mine"]);
        Assert.Equal("kept", (string?)j["other"]);
        Assert.Equal("claude-haiku-5", Env(h, "ANTHROPIC_DEFAULT_HAIKU_MODEL"));
        Assert.Equal("claude-haiku-5", cfg.Profiles["work"].Vertex!.HaikuModel);
        Assert.Equal("claude-haiku-5", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.HaikuModel);

        var r2 = syncer.Sync("work");
        Assert.Empty(r2.Notices);
        Assert.Equal("claude-haiku-5", Env(h, "ANTHROPIC_DEFAULT_HAIKU_MODEL"));
    }

    [Fact]
    public void LoginProfile_ModelKeyChange_NotAdopted()
    {
        var (cfg, syncer, h) = Setup(vertex: false); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"] = new JsonObject { [Opus] = "claude-opus-5-5" });

        var r = syncer.Sync("work");
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Null(cfg.Profiles["work"].Vertex);
        Assert.Null(ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex);
        Assert.False(ReadProfile(h).ContainsKey("env"));
    }

    [Fact]
    public void PendingSessionValue_NotAdoptedOverNewerConfigSave()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");                                      // writes Opus W
        SessionEdit(h, j => j["env"]![Opus] = "claude-opus-5-5"); // session writes Y
        cfg.Profiles["work"].Vertex!.OpusModel = "claude-opus-6"; // window saves Z, then syncs
        ConfigStore.Save(h.Paths, cfg);

        var r = syncer.Sync("work");
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Equal("claude-opus-6", Env(h, Opus));
        Assert.Equal("claude-opus-6", cfg.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-6", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }

    [Fact]
    public void LoginToVertex_LeftoverModelKey_NotAdopted()
    {
        var (cfg, syncer, h) = Setup(vertex: false); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"] = new JsonObject
        {
            [Opus] = "claude-opus-5-5",
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "claude-haiku-5",
        });
        cfg.Profiles["work"].Provider = Providers.Vertex;
        cfg.Profiles["work"].Vertex = new VertexSettings { Project = "proj", Region = "eu", OpusModel = "claude-opus-4-1" };
        ConfigStore.Save(h.Paths, cfg);

        var r = syncer.Sync("work");
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Equal("claude-opus-4-1", Env(h, Opus));
        Assert.Null(Env(h, "ANTHROPIC_DEFAULT_HAIKU_MODEL"));
        var saved = ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!;
        Assert.Equal("claude-opus-4-1", saved.OpusModel);
        Assert.Null(saved.HaikuModel);
    }

    [Fact]
    public void LegacyState_FallsBackToConfigured_AndAdopts()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        var st = SyncState.Load(Dir(h));
        st.ManagedModels = null; // state written before ManagedModels existed
        st.Save(Dir(h));
        SessionEdit(h, j => j["env"]![Opus] = "claude-opus-5-5");

        var r = syncer.Sync("work");
        Assert.Equal("work: Opus model updated to claude-opus-5-5 by Claude.", Assert.Single(r.Notices));
        Assert.Equal("claude-opus-5-5", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-5-5", Env(h, Opus));
    }

    [Fact]
    public void Adopt_UnconfiguredKey_Shared_Trimmed()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h; // Haiku not configured
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]!["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = " claude-haiku-5 ");

        var r = syncer.Sync("work");
        Assert.Equal("work: Haiku model updated to claude-haiku-5 by Claude.", Assert.Single(r.Notices));
        Assert.Equal("claude-haiku-5", cfg.Profiles["work"].Vertex!.HaikuModel);
        Assert.Equal("claude-haiku-5", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.HaikuModel);
        Assert.Equal("claude-haiku-5", Env(h, "ANTHROPIC_DEFAULT_HAIKU_MODEL"));

        var r2 = syncer.Sync("work");
        Assert.Empty(r2.Notices);
    }

    [Fact]
    public void ConfigSaveFailure_ProblemReported_NothingAdopted()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]![Opus] = "claude-opus-5-5");

        SyncReport r;
        using (new FileStream(h.Paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read)) // blocks the replace
            r = syncer.Sync("work");

        Assert.StartsWith("work: could not save the model Claude chose: ", Assert.Single(r.Problems));
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Equal("claude-opus-4-1", cfg.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-4-1", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
        Assert.Equal("claude-opus-4-1", Env(h, Opus));
    }

    [Fact]
    public void Adopt_BlankValue_NotAdopted()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        syncer.Sync("work");
        SessionEdit(h, j => j["env"]![Opus] = "  ");

        var r = syncer.Sync("work");
        Assert.DoesNotContain(r.Notices, n => n.Contains("model updated"));
        Assert.Equal("claude-opus-4-1", Env(h, Opus));
        Assert.Equal("claude-opus-4-1", ConfigStore.Load(h.Paths)!.Profiles["work"].Vertex!.OpusModel);
    }
}
