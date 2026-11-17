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
}
