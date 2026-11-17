using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class IsolatedTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Exe = @"C:\Program Files\clauderoo\clauderoo.exe";

    private static (ProfileManager pm, TempHome home) Make(bool vertex = false)
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        if (vertex)
            pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "europe-west1" });
        return (pm, home);
    }

    [Fact]
    public void PrepareIsolated_Vertex_WritesOverlayOnly_NoJunctions()
    {
        var (pm, home) = Make(vertex: true); using var _h = home;
        // Shared items exist in the real profile; none may show up in the isolated folder.
        pm.SyncAll();
        var (dir, _) = pm.PrepareIsolated("work");
        Assert.Equal(home.Paths.IsolatedDir("work"), dir);
        var entries = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToList();
        Assert.Equal([SyncState.FileName, "settings.json"], entries.Order(StringComparer.Ordinal).ToList());
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();
        Assert.Equal("1", s["env"]!["CLAUDE_CODE_USE_VERTEX"]!.GetValue<string>());
        Assert.Equal("proj-1", s["env"]!["ANTHROPIC_VERTEX_PROJECT_ID"]!.GetValue<string>());
        Assert.NotNull(s["gcpAuthRefresh"]);
        Assert.DoesNotContain(".credentials.json", entries);
    }

    [Fact]
    public void PrepareIsolated_Login_FirstRunTrue_ThenFalse()
    {
        var (pm, home) = Make(); using var _h = home;
        var (dir, first) = pm.PrepareIsolated("work");
        Assert.True(first);
        Assert.Equal("{}", JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.ToJsonString());
        Assert.True(pm.PrepareIsolated("work").FirstRun); // still no login
        File.WriteAllText(Path.Combine(dir, ".claude.json"), "{}");
        Assert.False(pm.PrepareIsolated("work").FirstRun);
    }

    [Fact]
    public void PrepareIsolated_KeepsUserSettings()
    {
        var (pm, home) = Make(vertex: true); using var _h = home;
        var (dir, _) = pm.PrepareIsolated("work");
        var f = Path.Combine(dir, "settings.json");
        var s = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        s["theme"] = "dark";
        File.WriteAllText(f, s.ToJsonString());
        pm.PrepareIsolated("work");
        var again = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Equal("dark", again["theme"]!.GetValue<string>());
        Assert.Equal("1", again["env"]!["CLAUDE_CODE_USE_VERTEX"]!.GetValue<string>());
    }

    [Fact]
    public void PrepareIsolated_Home_Allowed()
    {
        var (pm, home) = Make(); using var _h = home;
        var (dir, first) = pm.PrepareIsolated("personal");
        Assert.Equal(home.Paths.IsolatedDir("personal"), dir);
        Assert.True(first);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void PrepareIsolated_UnknownProfile_Throws()
    {
        var (pm, home) = Make(); using var _h = home;
        Assert.Throws<ClauderooException>(() => pm.PrepareIsolated("nope"));
    }

    [Fact]
    public void Start_Isolated_UsesIsolatedDir()
    {
        var (pm, home) = Make(); using var _h = home;
        LaunchSpec? spec = null;
        var o = new StringWriter();
        var code = Commands.Run(["start", "work", home.Dir, "--isolated"], o, new StringWriter(), pm, _ => 0,
            s => { spec = s; return 0; }, wtAvailable: true, claudePath: "claude.cmd");
        Assert.Equal(0, code);
        Assert.Equal(home.Paths.IsolatedDir("work"), spec!.ConfigDirEnv);
        Assert.Equal("work", spec.ExtraEnv!["CLAUDEROO_PROFILE"]);
        Assert.Equal("1", spec.ExtraEnv["CLAUDEROO_ISOLATED"]);
        Assert.Contains("First isolated run for work: log in once; later runs reuse it.", o.ToString());
        Assert.True(Directory.Exists(home.Paths.IsolatedDir("work")));
    }

    [Fact]
    public void Start_NotIsolated_HasNoIsolatedEnv()
    {
        var (pm, home) = Make(); using var _h = home;
        LaunchSpec? spec = null;
        Commands.Run(["start", "work", home.Dir], new StringWriter(), new StringWriter(), pm, _ => 0,
            s => { spec = s; return 0; }, wtAvailable: true, claudePath: "claude.cmd");
        Assert.False(spec!.ExtraEnv!.ContainsKey("CLAUDEROO_ISOLATED"));
        Assert.False(Directory.Exists(home.Paths.IsolatedDir("work")));
    }

    [Fact]
    public void NewWindow_Isolated_PassesFlag()
    {
        using var home = new TempHome();
        var cfg = new ClauderooConfig { Profiles = new() { ["personal"] = new ProfileEntry { Home = true }, ["work"] = new ProfileEntry() } };
        var s = Launcher.NewWindow(cfg, home.Paths, "work", @"C:\proj", ["--model", "opus"], true, Exe, isolated: true);
        var iso = home.Paths.IsolatedDir("work");
        Assert.Equal($"set CLAUDE_CONFIG_DIR={iso}&& \"{Exe}\" run work --isolated -- --model opus", s.Args[^1]);
        Assert.Equal(iso, s.ConfigDirEnv);
        var plain = Launcher.NewWindow(cfg, home.Paths, "work", @"C:\proj", [], true, Exe);
        Assert.DoesNotContain("--isolated", plain.Args[^1]);
        var noWt = Launcher.NewWindow(cfg, home.Paths, "work", @"C:\proj", [], false, Exe, isolated: true);
        Assert.Contains("--isolated", noWt.Args);
    }

    [Fact]
    public void Run_AcceptsIsolatedFlag_InTab()
    {
        var (pm, home) = Make(); using var _h = home;
        LaunchSpec? spec = null;
        var code = Commands.Run(["start", "work", home.Dir, "--tab", "--isolated"], new StringWriter(), new StringWriter(), pm,
            s => { spec = s; return 0; }, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        Assert.Equal(0, code);
        Assert.Contains(" run work --isolated", spec!.Args[^1]);
    }

    [Fact]
    public void Rename_MovesIsolatedDir()
    {
        var (pm, home) = Make(); using var _h = home;
        var (dir, _) = pm.PrepareIsolated("work");
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        pm.Rename("work", "job");
        Assert.False(Directory.Exists(home.Paths.IsolatedDir("work")));
        Assert.True(File.Exists(Path.Combine(home.Paths.IsolatedDir("job"), "marker.txt")));
    }

    [Fact]
    public void Rename_IsolatedTargetExists_RefusesBeforeAnyChange()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.PrepareIsolated("work");
        Directory.CreateDirectory(home.Paths.IsolatedDir("job"));
        Assert.Throws<ClauderooException>(() => pm.Rename("work", "job"));
        Assert.True(Directory.Exists(home.Paths.ProfileDir("work")));
        Assert.True(Directory.Exists(home.Paths.IsolatedDir("work")));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Rename_RollbackRestoresIsolatedDir()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.PrepareIsolated("work");
        pm.BeforeRenameCommit = () => throw new IOException("boom");
        Assert.Throws<IOException>(() => pm.Rename("work", "job"));
        Assert.True(Directory.Exists(home.Paths.IsolatedDir("work")));
        Assert.False(Directory.Exists(home.Paths.IsolatedDir("job")));
        Assert.True(Directory.Exists(home.Paths.ProfileDir("work")));
    }

    [Fact]
    public void Remove_MovesIsolatedDirAside()
    {
        var (pm, home) = Make(); using var _h = home;
        var (dir, _) = pm.PrepareIsolated("work");
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        pm.Remove("work");
        Assert.False(Directory.Exists(dir));
        var moved = Path.Combine(home.Paths.RemovedDir, "work-20261117-100000-isolated");
        Assert.True(File.Exists(Path.Combine(moved, "marker.txt")));
        Assert.True(Directory.Exists(Path.Combine(home.Paths.RemovedDir, "work-20261117-100000")));
    }

    [Fact]
    public void SyncAll_DoesNotTouchIsolated()
    {
        var (pm, home) = Make(vertex: true); using var _h = home;
        var (dir, _) = pm.PrepareIsolated("work");
        var f = Path.Combine(dir, "settings.json");
        var before = File.ReadAllText(f);
        Directory.CreateDirectory(Path.Combine(home.Paths.IsolatedDir("work"), "keep"));
        pm.SyncAll();
        Assert.Equal(before, File.ReadAllText(f));
        Assert.Equal([SyncState.FileName, "keep", "settings.json"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList());
    }
}
