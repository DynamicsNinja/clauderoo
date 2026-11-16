using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class FinalFixTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        pm.Init();
        return (pm, home);
    }

    private static void WritePluginRecord(TempHome h, string file, string content)
    {
        var dir = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true);
        return (code, o.ToString(), e.ToString());
    }

    // 2
    [Fact]
    public void Remove_UnlinksJunctions_LeavesHomeIntact()
    {
        var (pm, h) = Make(); using var _h = h;
        var plugins = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "keep.txt"), "x");
        pm.Add("work");
        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("work"), "plugins")));

        pm.Remove("work");

        var moved = Path.Combine(h.Paths.RemovedDir, "work-20261116-100000");
        Assert.True(Directory.Exists(moved));
        foreach (var d in Directory.GetDirectories(moved, "*", SearchOption.AllDirectories))
            Assert.Null(new DirectoryInfo(d).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(moved, "plugins")));
        Assert.True(File.Exists(Path.Combine(plugins, "keep.txt")));
    }

    // 12
    [Fact]
    public void Remove_MoveFails_ThrowsFriendlyError()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("work");
        var locked = Path.Combine(h.Paths.ProfileDir("work"), "busy.txt");
        File.WriteAllText(locked, "x");
        using var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ex = Assert.Throws<ClauderooException>(() => pm.Remove("work"));
        Assert.Equal("Couldn't move work's folder — close work's Claude sessions and try again.", ex.Message);
    }

    // 5
    [Fact]
    public void Remove_RefusesWhenPluginRecordsReferenceProfile()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("work");
        var dir = h.Paths.ProfileDir("work");
        WritePluginRecord(h, "installed_plugins.json",
            "{\"plugins\":{\"x\":[{\"installPath\":\"" + Json(dir.ToUpperInvariant()) + "\\\\plugins\\\\cache\\\\x\"}]}}");
        var ex = Assert.Throws<ClauderooException>(() => pm.Remove("work"));
        Assert.Contains("referenced by shared plugin records", ex.Message);
        Assert.Contains("--force", ex.Message);
        Assert.True(Directory.Exists(dir));

        pm.Remove("work", force: true);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Remove_SiblingPrefixInPluginRecords_IsNotAReference()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("work");
        WritePluginRecord(h, "known_marketplaces.json",
            "{\"m\":{\"installLocation\":\"" + Json(h.Paths.ProfileDir("work2")) + "\\\\x\"}}");
        pm.Remove("work");
        Assert.False(Directory.Exists(h.Paths.ProfileDir("work")));
    }

    [Fact]
    public void Cli_Remove_Force()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("work");
        WritePluginRecord(h, "known_marketplaces.json",
            "{\"m\":{\"installLocation\":\"" + Json(h.Paths.ProfileDir("work")) + "\"}}");
        var r1 = Run(pm, "remove", "work");
        Assert.Equal(1, r1.code);
        Assert.Contains("--force", r1.e);
        var r2 = Run(pm, "remove", "work", "--force");
        Assert.Equal(0, r2.code);
    }

    // 3
    [Fact]
    public void SyncAll_ContinuesPastLockedStateFile()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("a"); pm.Add("b");
        File.WriteAllText(Path.Combine(h.Paths.DefaultHomeDir, "settings.json"), "HOME");
        var stateA = Path.Combine(h.Paths.ProfileDir("a"), SyncState.FileName);
        Assert.True(File.Exists(stateA));
        using var lockA = new FileStream(stateA, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = pm.SyncAll();

        Assert.Contains(report.Problems, p => p.StartsWith("a: could not"));
        Assert.Equal("HOME", File.ReadAllText(Path.Combine(h.Paths.ProfileDir("b"), "settings.json")));
    }

    [Fact]
    public void Cli_LockedState_CommandStillSucceeds()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("a");
        using var lockA = new FileStream(Path.Combine(h.Paths.ProfileDir("a"), SyncState.FileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var r = Run(pm, "list");
        Assert.Equal(0, r.code);
        Assert.Contains("warning: a: could not", r.e);
    }

    [Fact]
    public void SyncState_Save_LeavesNoTmpFiles()
    {
        using var h = new TempHome();
        var dir = h.Paths.ProfileDir("w"); Directory.CreateDirectory(dir);
        new SyncState().Save(dir);
        new SyncState().Save(dir);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    // 4
    [Fact]
    public void ProblemTracker_OnlyNewProblems()
    {
        var t = new ProblemTracker();
        Assert.Equal(["a", "b"], t.NewOnly(["a", "b"]));
        Assert.Equal(["c"], t.NewOnly(["a", "c"]));
        Assert.Empty(t.NewOnly(["a", "c"]));
        Assert.Equal(["b"], t.NewOnly(["b"]));
    }

    private static (Syncer syncer, TempHome h) McpSetup()
    {
        var h = new TempHome();
        var cfg = new ClauderooConfig
        {
            HomeDir = h.Paths.DefaultHomeDir,
            HomeJson = h.Paths.DefaultHomeJson,
            Profiles = { ["personal"] = new ProfileEntry { Home = true }, ["work"] = new ProfileEntry() },
        };
        File.WriteAllText(h.Paths.DefaultHomeJson, "{\"mcpServers\":{\"s\":{\"command\":\"x\"}}}");
        Directory.CreateDirectory(h.Paths.ProfileDir("work"));
        return (new Syncer(cfg, h.Paths, () => Now), h);
    }

    // 8
    [Fact]
    public void Mcp_ProfileJsonChangesBeforeCommit_IsReReadAndMerged()
    {
        var (syncer, h) = McpSetup(); using var _h = h;
        var file = Path.Combine(h.Paths.ProfileDir("work"), ".claude.json");
        File.WriteAllText(file, "{}");
        var calls = 0;
        syncer.BeforeMcpCommit = () =>
        {
            if (calls++ == 0)
            {
                File.WriteAllText(file, "{\"oauthAccount\":{\"emailAddress\":\"a@b.c\"}}");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            }
        };
        var r = syncer.Sync("work");
        var root = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("a@b.c", (string?)root["oauthAccount"]!["emailAddress"]);
        Assert.NotNull(root["mcpServers"]!["s"]);
        Assert.Empty(r.Problems);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Mcp_ProfileJsonKeepsChanging_ReportsProblem()
    {
        var (syncer, h) = McpSetup(); using var _h = h;
        var file = Path.Combine(h.Paths.ProfileDir("work"), ".claude.json");
        File.WriteAllText(file, "{}");
        var n = 0;
        syncer.BeforeMcpCommit = () =>
        {
            File.WriteAllText(file, "{\"n\":" + (++n) + "}");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(n));
        };
        var r = syncer.Sync("work");
        Assert.Contains("work: .claude.json kept changing; MCP servers not synced", r.Problems);
        Assert.Empty(Directory.GetFiles(h.Paths.ProfileDir("work"), "*.tmp"));
    }

    // 9
    [Fact]
    public void Install_RefusesWhenDllNextToExe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clauderoo-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "clauderoo.dll"), "x");
            var ex = Assert.Throws<ClauderooException>(() => Installer.Install(dir));
            Assert.Contains("single-file", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    // 10
    [Fact]
    public void AutoStart_ResolveExe_PrefersInstalledCopy()
    {
        var local = Path.Combine(Path.GetTempPath(), "clauderoo-la-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(@"C:\x\clauderoow.exe", AutoStart.ResolveExe(local, @"C:\x\clauderoow.exe"));
            Directory.CreateDirectory(Path.Combine(local, "clauderoo"));
            var installed = Path.Combine(local, "clauderoo", "clauderoow.exe");
            File.WriteAllText(installed, "x");
            Assert.Equal(installed, AutoStart.ResolveExe(local, @"C:\x\clauderoow.exe"));
        }
        finally { if (Directory.Exists(local)) Directory.Delete(local, true); }
    }

    // 11
    [Fact]
    public void SetDefault_ReturnsSyncNotices()
    {
        var (pm, h) = Make(); using var _h = h;
        File.WriteAllText(Path.Combine(h.Paths.DefaultHomeDir, "settings.json"), "HOME");
        pm.Add("work");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "settings.json"), "MINE");
        var report = pm.SetDefault("work");
        Assert.Contains(report.Notices, n => n.Contains("settings.json"));
    }

    // 7
    [Fact]
    public void Cli_Status_PrintsEachProblemOnce()
    {
        var (pm, h) = Make(); using var _h = h;
        Directory.CreateDirectory(Path.Combine(h.Paths.DefaultHomeDir, "skills"));
        pm.Add("work");
        var link = Path.Combine(h.Paths.ProfileDir("work"), "skills");
        Directory.Delete(link);
        Directory.CreateDirectory(link);
        var r = Run(pm, "status");
        Assert.Equal(0, r.code);
        var lines = r.e.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains("skills is a real folder"));
        Assert.Equal(1, lines);
    }
}
