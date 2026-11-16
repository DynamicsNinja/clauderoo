using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make(bool withWork = true)
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        pm.Init();
        if (withWork) pm.Add("work");
        return (pm, home);
    }

    private static (int code, string o, string e, List<LaunchSpec> launched) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var launched = new List<LaunchSpec>();
        var code = Commands.Run(args, o, e, pm, s => { launched.Add(s); return 0; }, s => { launched.Add(s); return 7; }, wtAvailable: true);
        return (code, o.ToString(), e.ToString(), launched);
    }

    [Fact]
    public void Start_UnknownProfile_Exit1()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "start", "nope");
        Assert.Equal(1, r.code);
        Assert.Contains("Unknown profile", r.e);
        Assert.Empty(r.launched);
    }

    [Fact]
    public void Start_Work_LaunchesWithCwd()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "start", "work");
        Assert.Equal(0, r.code);
        var spec = Assert.Single(r.launched);
        Assert.Equal(Environment.CurrentDirectory, spec.WorkingDir);
        Assert.Equal(home.Paths.ProfileDir("work"), spec.ConfigDirEnv);
    }

    [Fact]
    public void Start_PassesArgsAfterDashDash()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "start", "work", "--", "--resume", "abc");
        Assert.Equal(0, r.code);
        var spec = Assert.Single(r.launched);
        Assert.Contains("claude --resume abc", spec.Args[^1]);
    }

    [Fact]
    public void Run_ReturnsClaudeExitCode_ForUnknownProfileIs1()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "run", "nope");
        Assert.Equal(1, r.code);
        Assert.Empty(r.launched);
    }

    [Fact]
    public void List_MarksDefault()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "list");
        Assert.Equal(0, r.code);
        var lines = r.o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.StartsWith("* personal  (not logged in)  (home)", lines[0]);
        Assert.StartsWith("  work      (not logged in)", lines[1]);
    }

    [Fact]
    public void NoArgs_PrintsUsage_Exit0()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm);
        Assert.Equal(0, r.code);
        Assert.Contains("Usage:", r.o);
        var h = Run(pm, "help");
        Assert.Equal(0, h.code);
        Assert.Contains("Usage:", h.o);
    }

    [Fact]
    public void Command_PrintsSyncNotices()
    {
        var (pm, home) = Make();
        using var _h = home;
        // Home's settings.json changes after the work profile has a divergent copy -> overwrite notice.
        File.WriteAllText(Path.Combine(home.Paths.DefaultHomeDir, "settings.json"), "{\"a\":1}");
        Run(pm, "list");
        File.WriteAllText(Path.Combine(home.Paths.ProfileDir("work"), "settings.json"), "{\"mine\":1}");
        File.WriteAllText(Path.Combine(home.Paths.DefaultHomeDir, "settings.json"), "{\"a\":2}");
        var r = Run(pm, "list");
        Assert.Contains("your changes to settings.json were replaced", r.o);
    }

    [Fact]
    public void UnknownCommand_Exit1()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, "bogus");
        Assert.Equal(1, r.code);
        Assert.Contains("Unknown command", r.e);
    }
}
