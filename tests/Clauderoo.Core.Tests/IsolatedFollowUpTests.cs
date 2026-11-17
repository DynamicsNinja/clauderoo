using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class IsolatedFollowUpTests
{
    private static (ProfileManager pm, TempHome home) Make(Func<DateTime>? now = null)
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), now ?? (() => new DateTime(2026, 11, 17, 10, 0, 0)),
            new FakeSecretStore(), new FakeGcloud()) { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    [Fact]
    public void Remove_ReadsStampOnce_ForBothFolders()
    {
        var t = new DateTime(2026, 11, 17, 10, 0, 0);
        var (pm, home) = Make(() => t = t.AddSeconds(1)); using var _h = home;
        pm.PrepareIsolated("work");
        pm.Remove("work");
        var names = Directory.GetDirectories(home.Paths.RemovedDir).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(names[0] + "-isolated", names[1]);
    }

    [Fact]
    public void Remove_IsolatedMoveFails_RollsBack()
    {
        var (pm, home) = Make(); using var _h = home;
        var (iso, _) = pm.PrepareIsolated("work");
        var locked = Path.Combine(iso, "busy.txt");
        File.WriteAllText(locked, "x");
        using var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ex = Assert.Throws<ClauderooException>(() => pm.Remove("work"));
        Assert.StartsWith("Couldn't move work's isolated folder", ex.Message);
        Assert.True(Directory.Exists(home.Paths.ProfileDir("work")));
        Assert.False(File.Exists(Path.Combine(home.Paths.ProfileDir("work"), ProfileManager.EntryFileName)));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Remove_IsolatedMoveAndRollbackFail_EntrySaved_ErrorNamesFolder()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        var (iso, _) = pm.PrepareIsolated("work");
        var locked = Path.Combine(iso, "busy.txt");
        File.WriteAllText(locked, "x");
        using var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Something takes the profile's old path, so moving the folder back fails.
        pm.BeforeIsolatedMove = () => Directory.CreateDirectory(home.Paths.ProfileDir("work"));
        var ex = Assert.Throws<ClauderooException>(() => pm.Remove("work"));
        var moved = Path.Combine(home.Paths.RemovedDir, "work-20261117-100000");
        Assert.Contains(moved, ex.Message);
        var entry = ConfigStore.ReadEntry(Path.Combine(moved, ProfileManager.EntryFileName));
        Assert.NotNull(entry);
        Assert.Equal(Providers.Vertex, entry!.Provider);
        Assert.Equal("proj-1", entry.Vertex!.Project);
    }

    [Fact]
    public void PrepareIsolated_RemovedEnvVar_IsRemoved()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetEnv("work", [new("MY_VAR", "1")], []);
        var (dir, _) = pm.PrepareIsolated("work");
        var f = Path.Combine(dir, "settings.json");
        Assert.Equal("1", JsonNode.Parse(File.ReadAllText(f))!["env"]!["MY_VAR"]!.GetValue<string>());
        pm.SetEnv("work", [], ["MY_VAR"]);
        pm.PrepareIsolated("work");
        var s = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Null(s["env"]?["MY_VAR"]);
    }

    [Fact]
    public void PrepareIsolated_SwitchFromApiKey_RemovesHelper()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-ant-api-x");
        var (dir, _) = pm.PrepareIsolated("work");
        var f = Path.Combine(dir, "settings.json");
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(f))!["apiKeyHelper"]);
        pm.SetProvider("work", Providers.Login);
        pm.PrepareIsolated("work");
        Assert.Null(JsonNode.Parse(File.ReadAllText(f))!["apiKeyHelper"]);
    }

    [Fact]
    public void Rename_StrayIsolatedTarget_Refused()
    {
        var (pm, home) = Make(); using var _h = home;
        Directory.CreateDirectory(home.Paths.IsolatedDir("job"));
        Assert.Throws<ClauderooException>(() => pm.Rename("work", "job"));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
        Assert.True(Directory.Exists(home.Paths.ProfileDir("work")));
    }
}
