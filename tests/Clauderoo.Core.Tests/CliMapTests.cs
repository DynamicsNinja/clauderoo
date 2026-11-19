using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliMapTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    private static (int code, string o, string e, List<LaunchSpec> launched) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var launched = new List<LaunchSpec>();
        var code = Commands.Run(args, o, e, pm, s => { launched.Add(s); return 0; }, s => { launched.Add(s); return 7; },
            wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString(), launched);
    }

    private static string Sub(TempHome home, string name)
    {
        var d = Path.Combine(home.Paths.Root, "proj", name);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Start_NoProfile_UsesMapping()
    {
        var (pm, home) = Make();
        using var _h = home;
        var mapped = Sub(home, "company");
        pm.Map("work", mapped);
        var inner = Path.Combine(mapped, "app");
        Directory.CreateDirectory(inner);
        var r = Run(pm, "start", inner);
        Assert.Equal(7, r.code);
        var spec = Assert.Single(r.launched);
        Assert.Equal("work", spec.Profile);
        Assert.Equal(inner, spec.WorkingDir);
        Assert.Contains("Using work (mapped from", r.o);
    }

    [Fact]
    public void Start_NoProfile_NoMapping_UsesDefault()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "plain");
        var r = Run(pm, "start", dir);
        var spec = Assert.Single(r.launched);
        Assert.Equal("personal", spec.Profile);
        Assert.DoesNotContain("mapped from", r.o);
    }

    [Fact]
    public void Start_ProfileNameWinsOverFolder()
    {
        var (pm, home) = Make();
        using var _h = home;
        var mapped = Sub(home, "company");
        pm.Map("personal", mapped);
        var r = Run(pm, "start", "work", mapped);
        Assert.Equal("work", Assert.Single(r.launched).Profile);
    }

    [Fact]
    public void Map_Unmap_Maps_RoundTrip()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        Assert.Equal(0, Run(pm, "map", "work", dir).code);
        var list = Run(pm, "maps");
        Assert.Contains("work", list.o);
        Assert.Contains(FolderMap.Normalize(dir), list.o);
        Assert.Equal(0, Run(pm, "unmap", dir).code);
        Assert.DoesNotContain("work", Run(pm, "maps").o);
    }

    private static (int code, string o, List<LaunchSpec> launched) Ask(ProfileManager pm, string? answer, params string[] args)
    {
        var o = new StringWriter();
        var launched = new List<LaunchSpec>();
        var asked = 0;
        var code = Commands.Run(args, o, new StringWriter(), pm, s => { launched.Add(s); return 0; }, s => { launched.Add(s); return 0; },
            wtAvailable: true, claudePath: "claude.cmd", hooks: new ReleaseHooks { ReadLine = () => { asked++; return answer; } });
        return (code, o.ToString() + (asked > 0 ? "" : "<not asked>"), launched);
    }

    [Fact]
    public void Start_Unmapped_YesMapsFolder()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "fresh");
        var r = Ask(pm, "", "start", "work", dir);
        Assert.Equal("work", Assert.Single(r.launched).Profile);
        Assert.Contains("isn't mapped", r.o);
        Assert.Equal("work", FolderMap.Resolve(pm.Config, dir)?.Profile);
    }

    [Fact]
    public void Start_Unmapped_NoLeavesItUnmapped_NeverStopsAsking()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "fresh");
        Ask(pm, "n", "start", "work", dir);
        Assert.Null(FolderMap.Resolve(pm.Config, dir));
        Ask(pm, "never", "start", "work", dir);
        Assert.Null(FolderMap.Resolve(pm.Config, dir));
        var r = Ask(pm, "y", "start", "work", dir);
        Assert.EndsWith("<not asked>", r.o);
        Assert.Null(FolderMap.Resolve(pm.Config, dir));
    }

    [Fact]
    public void Start_Mapped_SameProfile_DoesNotAsk()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        pm.Map("work", dir);
        Assert.EndsWith("<not asked>", Ask(pm, "y", "start", "work", dir).o);
        Assert.EndsWith("<not asked>", Ask(pm, "y", "start", dir).o);
    }

    [Fact]
    public void Start_Mapped_OtherProfile_DefaultSwitchesToMapped()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        pm.Map("work", dir);
        var r = Ask(pm, "", "start", "personal", dir);
        Assert.Contains("is mapped to 'work'", r.o);
        Assert.Equal("work", Assert.Single(r.launched).Profile);
    }

    [Fact]
    public void Start_Mapped_OtherProfile_YesKeepsChoice_RemapChangesMapping()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        pm.Map("work", dir);
        Assert.Equal("personal", Assert.Single(Ask(pm, "y", "start", "personal", dir).launched).Profile);
        Assert.Equal("work", FolderMap.Resolve(pm.Config, dir)?.Profile);
        Assert.Equal("personal", Assert.Single(Ask(pm, "r", "start", "personal", dir).launched).Profile);
        Assert.Equal("personal", FolderMap.Resolve(pm.Config, dir)?.Profile);
    }

    [Fact]
    public void Start_Mapped_OtherProfile_NoAnswerStartsNothing()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        pm.Map("work", dir);
        var r = Ask(pm, null, "start", "personal", dir);
        Assert.Equal(1, r.code);
        Assert.Empty(r.launched);
    }

    [Fact]
    public void Run_NeverAsks()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Sub(home, "company");
        pm.Map("work", dir);
        var r = Ask(pm, "", "run", "personal", dir);
        Assert.EndsWith("<not asked>", r.o);
        Assert.Equal("personal", Assert.Single(r.launched).Profile);
    }
}
