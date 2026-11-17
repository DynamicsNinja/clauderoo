namespace Clauderoo.Core.Tests;

public class WhichTests
{
    private static ClauderooConfig Cfg(string? def = null) => new()
    {
        DefaultProfile = def,
        Profiles = new()
        {
            ["personal"] = new ProfileEntry { Home = true },
            ["work"] = new ProfileEntry(),
        },
    };

    private static Func<string, string?> Env(params (string k, string v)[] vars)
    {
        var d = vars.ToDictionary(x => x.k, x => x.v);
        return n => d.GetValueOrDefault(n);
    }

    [Fact]
    public void Which_FromSessionVar()
    {
        using var home = new TempHome();
        var r = WhichProfile.Resolve(Cfg(), home.Paths, Env(("CLAUDEROO_PROFILE", "work")), new FakeEnv());
        Assert.Equal(new WhichResult("work", "session", false), r);
    }

    [Fact]
    public void Which_FromConfigDirMatch()
    {
        using var home = new TempHome();
        var dir = home.Paths.ProfileDir("work") + "\\";
        var r = WhichProfile.Resolve(Cfg(), home.Paths, Env(("CLAUDE_CONFIG_DIR", dir.ToUpperInvariant())), new FakeEnv());
        Assert.Equal(new WhichResult("work", "configDir", false), r);
    }

    [Fact]
    public void Which_FallsBackToDefault()
    {
        using var home = new TempHome();
        var env = new FakeEnv();
        env.User["CLAUDE_CONFIG_DIR"] = home.Paths.ProfileDir("work");
        var r = WhichProfile.Resolve(Cfg("work"), home.Paths, Env(), env);
        Assert.Equal(new WhichResult("work", "default", false), r);
    }

    [Fact]
    public void Which_NoConfigDir_IsHome()
    {
        using var home = new TempHome();
        var r = WhichProfile.Resolve(Cfg("work"), home.Paths, Env(), new FakeEnv());
        Assert.Equal("home", r.Source);
        Assert.Equal("personal", r.Profile);
        Assert.False(r.Isolated);
    }

    [Fact]
    public void Which_IsolatedConfigDir_FlagsIsolated()
    {
        using var home = new TempHome();
        var r = WhichProfile.Resolve(Cfg(), home.Paths,
            Env(("CLAUDEROO_PROFILE", "work"), ("CLAUDE_CONFIG_DIR", home.Paths.IsolatedDir("work"))), new FakeEnv());
        Assert.Equal(new WhichResult("work", "session", true), r);

        var r2 = WhichProfile.Resolve(Cfg(), home.Paths, Env(("CLAUDE_CONFIG_DIR", home.Paths.IsolatedDir("work"))), new FakeEnv());
        Assert.Equal(new WhichResult("work", "configDir", true), r2);
    }

    [Fact]
    public void Which_UnknownSessionVar_FallsThrough()
    {
        using var home = new TempHome();
        var r = WhichProfile.Resolve(Cfg(), home.Paths, Env(("CLAUDEROO_PROFILE", "gone")), new FakeEnv());
        Assert.Equal("home", r.Source);
    }
}
