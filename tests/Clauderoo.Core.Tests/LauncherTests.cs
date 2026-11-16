namespace Clauderoo.Core.Tests;

public class LauncherTests
{
    private static ClauderooConfig Cfg() => new()
    {
        Profiles = new()
        {
            ["personal"] = new ProfileEntry { Home = true },
            ["work"] = new ProfileEntry(),
        },
    };

    [Fact]
    public void Wt_Work_BuildsNewTab()
    {
        using var home = new TempHome();
        var workDir = home.Paths.ProfileDir("work");
        var dir = @"C:\proj";
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", dir, [], wtAvailable: true);
        Assert.Equal("wt.exe", s.FileName);
        Assert.Equal(new[] { "-w", "0", "new-tab", "--title", "Claude · work", "-d", dir, "cmd", "/k",
            $"set \"CLAUDE_CONFIG_DIR={workDir}\" && claude" }, s.Args);
        Assert.Equal(workDir, s.ConfigDirEnv);
        Assert.Equal(dir, s.WorkingDir);
    }

    [Fact]
    public void Wt_Home_ClearsVar()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "personal", @"C:\proj", [], true);
        Assert.StartsWith("set \"CLAUDE_CONFIG_DIR=\" && claude", s.Args[^1]);
        Assert.Null(s.ConfigDirEnv);
    }

    [Fact]
    public void Wt_PathWithSpaces_PassedAsSingleArgs()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\My Projects", [], true);
        Assert.Single(s.Args, a => a == @"C:\My Projects");
    }

    [Fact]
    public void Wt_ClaudeArgs_Appended()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["--model", "opus"], true);
        Assert.EndsWith("claude --model opus", s.Args[^1]);
    }

    [Fact]
    public void Wt_ClaudeArgWithSpaces_Quoted()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["hello world"], true);
        Assert.EndsWith("claude \"hello world\"", s.Args[^1]);
    }

    [Fact]
    public void NoWt_UsesCmdStart()
    {
        using var home = new TempHome();
        var workDir = home.Paths.ProfileDir("work");
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", [], false);
        Assert.Equal("cmd.exe", s.FileName);
        Assert.Equal(new[] { "/c", "start", "Claude · work", "/D", @"C:\p", "cmd", "/k",
            $"set \"CLAUDE_CONFIG_DIR={workDir}\" && claude" }, s.Args);
    }

    [Fact]
    public void UnknownProfile_Throws()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "nope", @"C:\p", [], true));
        Assert.Throws<ClauderooException>(() => Launcher.InPlace(Cfg(), home.Paths, "nope", @"C:\p", []));
    }

    [Fact]
    public void ResolveClaude_FindsViaPathExt_AndThrowsWhenMissing()
    {
        using var home = new TempHome();
        var bin = Path.Combine(home.Dir, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "claude.cmd");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, Launcher.ResolveClaude(bin, ".EXE;.CMD"), ignoreCase: true);
        Assert.Throws<ClauderooException>(() => Launcher.ResolveClaude(Path.Combine(home.Dir, "empty"), ".EXE;.CMD"));
    }
}
