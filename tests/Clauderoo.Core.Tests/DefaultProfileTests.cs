namespace Clauderoo.Core.Tests;

public class DefaultProfileTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);
    private const string Var = "CLAUDE_CONFIG_DIR";

    private static (ProfileManager pm, TempHome home, FakeEnv env) Make()
    {
        var home = new TempHome();
        var env = new FakeEnv();
        var pm = new ProfileManager(home.Paths, env, () => Now);
        pm.Init();
        pm.Add("work");
        return (pm, home, env);
    }

    [Fact]
    public void SetDefault_Work_SetsUserVar()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        pm.SetDefault("work");
        Assert.Equal(home.Paths.ProfileDir("work"), env.User[Var]);
        Assert.Equal("work", pm.Config.DefaultProfile);
    }

    [Fact]
    public void SetDefault_Home_ClearsVar()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        pm.SetDefault("work");
        pm.SetDefault("personal");
        Assert.False(env.User.ContainsKey(Var));
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void SetDefault_Null_ClearsVar()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        pm.SetDefault("work");
        pm.SetDefault(null);
        Assert.False(env.User.ContainsKey(Var));
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void SetDefault_Unknown_Throws()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        Assert.Throws<ClauderooException>(() => pm.SetDefault("nope"));
        Assert.False(env.User.ContainsKey(Var));
    }

    [Fact]
    public void Status_EnvMismatch_Reported()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        pm.SetDefault("work");
        Assert.True(pm.Status().EnvMatchesDefault);
        env.User[Var] = @"C:\somewhere\else";
        Assert.False(pm.Status().EnvMatchesDefault);
    }
}
