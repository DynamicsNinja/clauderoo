namespace Clauderoo.Core.Tests;

public class LauncherTests
{
    private const string Exe = @"C:\Program Files\clauderoo\clauderoo.exe";

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
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", dir, [], wtAvailable: true, clauderooExe: Exe);
        Assert.Equal("wt.exe", s.FileName);
        Assert.Equal(new[] { "-w", "0", "new-tab", "--title", "Claude · work", "-d", dir, "cmd", "/k",
            $"set CLAUDE_CONFIG_DIR={workDir}&& \"{Exe}\" run work" }, s.Args);
        Assert.Equal(workDir, s.ConfigDirEnv);
        Assert.Equal(dir, s.WorkingDir);
    }

    [Fact]
    public void Wt_Home_ClearsVar()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "personal", @"C:\proj", [], true, Exe);
        Assert.StartsWith("set CLAUDE_CONFIG_DIR=&& \"" + Exe + "\" run personal", s.Args[^1]);
        Assert.Null(s.ConfigDirEnv);
    }

    [Fact]
    public void Wt_PathWithSpaces_PassedAsSingleArgs()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\My Projects", [], true, Exe);
        Assert.Single(s.Args, a => a == @"C:\My Projects");
    }

    [Fact]
    public void Wt_ClaudeArgs_Appended()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["--model", "opus"], true, Exe);
        Assert.EndsWith("run work -- --model opus", s.Args[^1]);
    }

    [Fact]
    public void Wt_ClaudeArgWithSpaces_Quoted()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["hello world"], true, Exe);
        Assert.EndsWith("run work -- \"hello world\"", s.Args[^1]);
    }

    [Fact]
    public void NoWt_UsesCmdStart()
    {
        using var home = new TempHome();
        var workDir = home.Paths.ProfileDir("work");
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", [], false, Exe);
        Assert.Equal("cmd.exe", s.FileName);
        Assert.Equal(new[] { "/c", "start", "Claude · work", "/D", @"C:\p", "cmd", "/k",
            Exe, "run", "work" }, s.Args);
        Assert.Equal(workDir, s.ConfigDirEnv);
        var withArgs = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["--model", "opus"], false, Exe);
        Assert.Equal(new[] { "/c", "start", "Claude · work", "/D", @"C:\p", "cmd", "/k", Exe, "run", "work", "--", "--model", "opus" }, withArgs.Args);
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", [], false, @"C:\Program Files (x86)\c\clauderoo.exe"));
    }

    [Fact]
    public void Wt_AmpersandArg_Quoted()
    {
        using var home = new TempHome();
        var s = Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["foo&calc"], true, Exe);
        Assert.EndsWith("run work -- \"foo&calc\"", s.Args[^1]);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("%PATH%")]
    [InlineData("a;b")]
    [InlineData("hi!")]
    public void BadClaudeArg_Throws(string arg)
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", [arg], true));
    }

    [Fact]
    public void NoWt_ArgNeedingQuotes_Throws()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["a b"], false));
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\p", ["x&y"], false));
    }

    [Fact]
    public void BadDir_Throws()
    {
        using var home = new TempHome();
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\a;b", [], true));
        Assert.Throws<ClauderooException>(() => Launcher.NewWindow(Cfg(), home.Paths, "work", @"C:\a&b", [], false));
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
