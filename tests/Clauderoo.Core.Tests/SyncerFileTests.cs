using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class SyncerFileTests
{
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
        return (cfg, new Syncer(cfg, h.Paths, () => new DateTime(2026, 11, 16, 10, 0, 0)), h);
    }

    private const string Bak = "settings.json.bak-20261116-100000";

    [Fact]
    public void Sync_CopiesSettingsFirstTime()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(Path.Combine(cfg.HomeDir, "settings.json"), "HOME");
        var r = syncer.Sync("work");
        Assert.Equal("HOME", File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json")));
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void Sync_HomeChanged_ProfileUnchanged_UpdatesSilently()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        var src = Path.Combine(cfg.HomeDir, "settings.json");
        File.WriteAllText(src, "A");
        syncer.Sync("work");
        File.WriteAllText(src, "B");
        var r = syncer.Sync("work");
        Assert.Equal("B", File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json")));
        Assert.Empty(r.Notices);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.bak-*"));
    }

    [Fact]
    public void Sync_ProfileChanged_BacksUpAndNotifies()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        File.WriteAllText(Path.Combine(cfg.HomeDir, "settings.json"), "HOME");
        syncer.Sync("work");
        var dir = h.Paths.ProfileDir("work");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "X");
        var r = syncer.Sync("work");
        Assert.Equal("X", File.ReadAllText(Path.Combine(dir, Bak)));
        Assert.Equal("HOME", File.ReadAllText(Path.Combine(dir, "settings.json")));
        Assert.Equal(
            $"work: your changes to settings.json were replaced by personal's (saved {Bak}). Use 'clauderoo own work settings.json' to keep work's own.",
            Assert.Single(r.Notices));
    }

    [Fact]
    public void Sync_MissingSourceFile_Skipped()
    {
        var (_, syncer, h) = Setup(); using var _ = h;
        var r = syncer.Sync("work");
        Assert.False(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "CLAUDE.md")));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Sync_OverriddenFile_NotTouched()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        cfg.Profiles["work"].Overrides.Add("settings.json");
        File.WriteAllText(Path.Combine(cfg.HomeDir, "settings.json"), "HOME");
        syncer.Sync("work");
        Assert.False(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "settings.json")));
    }

    private static void WriteHomeJson(TempHome h) =>
        File.WriteAllText(h.Paths.DefaultHomeJson, "{\"mcpServers\":{\"a\":{\"command\":\"x\"}},\"oauthAccount\":{\"emailAddress\":\"h@x\"}}");

    [Fact]
    public void Sync_Mcp_CreatesProfileJson()
    {
        var (_, syncer, h) = Setup(); using var _ = h;
        WriteHomeJson(h);
        syncer.Sync("work");
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(h.Paths.ProfileDir("work"), ".claude.json")))!;
        Assert.Equal("x", (string?)j["mcpServers"]!["a"]!["command"]);
        Assert.Null(j["oauthAccount"]);
    }

    [Fact]
    public void Sync_Mcp_PreservesOtherKeys()
    {
        var (_, syncer, h) = Setup(); using var _ = h;
        WriteHomeJson(h);
        var dir = h.Paths.ProfileDir("work");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"w@x\"},\"projects\":{\"p\":1}}");
        syncer.Sync("work");
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".claude.json")))!;
        Assert.Equal("w@x", (string?)j["oauthAccount"]!["emailAddress"]);
        Assert.Equal(1, (int?)j["projects"]!["p"]);
        Assert.NotNull(j["mcpServers"]!["a"]);
    }

    [Fact]
    public void Sync_Mcp_ProfileChanged_BacksUp()
    {
        var (_, syncer, h) = Setup(); using var _ = h;
        WriteHomeJson(h);
        syncer.Sync("work");
        var dir = h.Paths.ProfileDir("work");
        File.WriteAllText(Path.Combine(dir, ".claude.json"), "{\"mcpServers\":{\"mine\":{\"command\":\"y\"}}}");
        var r = syncer.Sync("work");
        var bak = Path.Combine(dir, "mcpServers.bak-20261116-100000.json");
        Assert.Contains("mine", File.ReadAllText(bak));
        Assert.Equal(
            "work: your changes to mcpServers were replaced by personal's (saved mcpServers.bak-20261116-100000.json). Use 'clauderoo own work mcpServers' to keep work's own.",
            Assert.Single(r.Notices));
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".claude.json")))!;
        Assert.NotNull(j["mcpServers"]!["a"]);
        Assert.Null(j["mcpServers"]!["mine"]);
    }

    [Fact]
    public void Sync_MalformedProfileJson_NotOverwritten()
    {
        var (_, syncer, h) = Setup(); using var _ = h;
        WriteHomeJson(h);
        var dir = h.Paths.ProfileDir("work");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, ".claude.json");
        File.WriteAllText(f, "{\"oops\"");
        var r = syncer.Sync("work");
        Assert.Equal("{\"oops\"", File.ReadAllText(f));
        Assert.Equal("work: .claude.json is not valid JSON; MCP servers not synced", Assert.Single(r.Problems));
    }

    private static Dictionary<string, (byte[] bytes, DateTime mtime)> Snapshot(ClauderooConfig cfg)
    {
        var d = new Dictionary<string, (byte[], DateTime)>();
        foreach (var f in Directory.GetFiles(cfg.HomeDir, "*", SearchOption.AllDirectories).Append(cfg.HomeJson))
            d[f] = (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f));
        return d;
    }

    [Fact]
    public void Sync_NeverWritesHome()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        WriteHomeJson(h);
        File.WriteAllText(Path.Combine(cfg.HomeDir, "settings.json"), "HOME");
        File.WriteAllText(Path.Combine(cfg.HomeDir, "CLAUDE.md"), "md");
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "skills"));
        File.WriteAllText(Path.Combine(cfg.HomeDir, "skills", "s.md"), "s");
        var before = Snapshot(cfg);
        syncer.Sync("work");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json"), "X");
        syncer.Sync("work");
        var after = Snapshot(cfg);
        Assert.Equal(before.Count, after.Count);
        foreach (var (k, v) in before)
        {
            Assert.Equal(v.bytes, after[k].bytes);
            Assert.Equal(v.mtime, after[k].mtime);
        }
    }

    [Fact]
    public void SyncAll_DanglingJunction_ReportsProblemWithoutThrowing()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "plugins"));
        var gone = Path.Combine(h.Dir, "gone");
        Directory.CreateDirectory(gone);
        var dir = h.Paths.ProfileDir("work");
        Directory.CreateDirectory(dir);
        Junction.Create(Path.Combine(dir, "plugins"), gone);
        Directory.Delete(gone);

        var r = syncer.SyncAll();

        Assert.Single(r.Problems, p => p.StartsWith("work: plugins is linked to"));
    }

    [Fact]
    public void Sync_FileAtLinkPath_ReportsFileNotFolder()
    {
        var (cfg, syncer, h) = Setup(); using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "plugins"));
        var dir = h.Paths.ProfileDir("work");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugins"), "x");
        var r = syncer.Sync("work");
        Assert.Equal("work: plugins is a file, not a shared folder", Assert.Single(r.Problems));
    }
}
