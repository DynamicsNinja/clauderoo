namespace Clauderoo.Core.Tests;

public class FakeEnv : IEnvStore
{
    public Dictionary<string, string> Process { get; } = new();
    public Dictionary<string, string> User { get; } = new();
    public string? GetProcess(string name) => Process.GetValueOrDefault(name);
    public string? GetUser(string name) => User.GetValueOrDefault(name);
    public void SetUser(string name, string? value)
    {
        if (value == null) User.Remove(name); else User[name] = value;
    }
}

public class ProfileManagerTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome home, FakeEnv env) Make()
    {
        var home = new TempHome();
        var env = new FakeEnv();
        return (new ProfileManager(home.Paths, env, () => Now), home, env);
    }

    [Fact]
    public void Init_RegistersHome()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        var cfg = pm.Config;
        Assert.True(cfg.Profiles["personal"].Home);
        Assert.Equal("personal", cfg.DefaultProfile);
        Assert.Equal(home.Paths.DefaultHomeDir, cfg.HomeDir);
        Assert.Equal(home.Paths.DefaultHomeJson, cfg.HomeJson);
    }

    [Fact]
    public void Init_EnvVarSet_Throws()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        env.User["CLAUDE_CONFIG_DIR"] = @"C:\x";
        var ex = Assert.Throws<ClauderooException>(() => pm.Init());
        Assert.Equal(@"CLAUDE_CONFIG_DIR is set to 'C:\x'. Clear it, or run 'clauderoo init --home <path>'.", ex.Message);
        env.User.Clear();
        env.Process["CLAUDE_CONFIG_DIR"] = @"C:\y";
        Assert.Throws<ClauderooException>(() => pm.Init());
    }

    [Fact]
    public void Init_EnvVarSet_WithHome_Ok()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        env.User["CLAUDE_CONFIG_DIR"] = @"C:\x";
        var custom = Path.Combine(home.Dir, "custom");
        Directory.CreateDirectory(custom);
        pm.Init("main", custom);
        Assert.Equal(custom, pm.Config.HomeDir);
        Assert.Equal(Path.Combine(custom, ".claude.json"), pm.Config.HomeJson);
        Assert.Equal("main", pm.Config.DefaultProfile);
    }

    [Fact]
    public void Init_MissingHomeDir_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        Assert.Throws<ClauderooException>(() => pm.Init("a", Path.Combine(home.Dir, "nope")));
    }

    [Fact]
    public void Init_Twice_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        Assert.Throws<ClauderooException>(() => pm.Init());
    }

    [Fact]
    public void Config_BeforeInit_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        var ex = Assert.Throws<ClauderooException>(() => pm.Config);
        Assert.Equal("Run 'clauderoo init' first.", ex.Message);
    }

    [Fact]
    public void Add_CreatesFolderAndJunctions()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        Directory.CreateDirectory(Path.Combine(home.Paths.DefaultHomeDir, "skills"));
        pm.Init();
        pm.Add("work");
        var dir = home.Paths.ProfileDir("work");
        Assert.True(Directory.Exists(dir));
        Assert.NotNull(Junction.GetTarget(Path.Combine(dir, "skills")));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Add_InvalidName_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        Assert.Throws<ClauderooException>(() => pm.Add("Bad Name"));
        Assert.False(Directory.Exists(home.Paths.ProfileDir("Bad Name")));
    }

    [Fact]
    public void Add_Duplicate_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        Assert.Throws<ClauderooException>(() => pm.Add("work"));
        Assert.Throws<ClauderooException>(() => pm.Add("personal"));
    }

    [Fact]
    public void Remove_MovesToRemoved()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        pm.Remove("work");
        Assert.False(Directory.Exists(home.Paths.ProfileDir("work")));
        Assert.True(Directory.Exists(Path.Combine(home.Paths.RemovedDir, "work-20261116-100000")));
        Assert.False(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Remove_Home_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        Assert.Throws<ClauderooException>(() => pm.Remove("personal"));
    }

    [Fact]
    public void Remove_Default_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init("main");
        pm.Add("work");
        var cfg = pm.Config;
        cfg.DefaultProfile = "work";
        ConfigStore.Save(home.Paths, cfg);
        Assert.Throws<ClauderooException>(() => pm.Remove("work"));
    }

    [Fact]
    public void List_ReadsEmail()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        File.WriteAllText(home.Paths.DefaultHomeJson, "{\"oauthAccount\":{\"emailAddress\":\"home@x\"}}");
        pm.Init();
        pm.Add("work");
        pm.Add("other");
        File.WriteAllText(Path.Combine(home.Paths.ProfileDir("work"), ".claude.json"),
            "{\"oauthAccount\":{\"emailAddress\":\"me@corp\"}}");
        var list = pm.List();
        Assert.Equal("home@x", list.Single(p => p.IsHome).Email);
        Assert.Equal("me@corp", list.Single(p => p.Name == "work").Email);
        Assert.Null(list.Single(p => p.Name == "other").Email);
        Assert.True(list.Single(p => p.IsHome).IsDefault);
    }

    [Fact]
    public void Own_Dir_ReplacesJunctionWithCopy_HomeIntact()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        var src = Path.Combine(home.Paths.DefaultHomeDir, "skills");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.md"), "A");
        File.WriteAllText(Path.Combine(src, "sub", "b.md"), "B");
        pm.Init();
        pm.Add("work");
        var link = Path.Combine(home.Paths.ProfileDir("work"), "skills");
        Assert.NotNull(Junction.GetTarget(link));

        pm.Own("work", "skills");

        Assert.Null(Junction.GetTarget(link));
        Assert.Equal("A", File.ReadAllText(Path.Combine(link, "a.md")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(link, "sub", "b.md")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(src, "a.md")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(src, "sub", "b.md")));
        Assert.Contains("skills", pm.Config.Profiles["work"].Overrides);
    }

    [Fact]
    public void Own_HomeOrUnknown_Throws()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        Assert.Throws<ClauderooException>(() => pm.Own("work", "bogus"));
        Assert.Throws<ClauderooException>(() => pm.Own("personal", "skills"));
    }

    [Fact]
    public void Share_Dir_BacksUpAndRelinks()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        var src = Path.Combine(home.Paths.DefaultHomeDir, "skills");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.md"), "A");
        pm.Init();
        pm.Add("work");
        pm.Own("work", "skills");
        var link = Path.Combine(home.Paths.ProfileDir("work"), "skills");
        File.WriteAllText(Path.Combine(link, "mine.md"), "M");

        pm.Share("work", "skills");

        Assert.NotNull(Junction.GetTarget(link));
        var bak = Path.Combine(home.Paths.ProfileDir("work"), "skills.bak-20261116-100000");
        Assert.Equal("M", File.ReadAllText(Path.Combine(bak, "mine.md")));
        Assert.DoesNotContain("skills", pm.Config.Profiles["work"].Overrides);
    }

    [Fact]
    public void Get_Unknown_ThrowsListingKnown()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        var ex = Assert.Throws<ClauderooException>(() => pm.Get("zzz"));
        Assert.Equal("Unknown profile 'zzz'. Known: personal, work.", ex.Message);
    }

    [Fact]
    public void Status_EnvMatchesDefault()
    {
        var (pm, home, env) = Make();
        using var _h = home;
        pm.Init();
        pm.Add("work");
        Assert.True(pm.Status().EnvMatchesDefault); // home default, env unset
        env.User["CLAUDE_CONFIG_DIR"] = home.Paths.ProfileDir("work");
        Assert.False(pm.Status().EnvMatchesDefault);
        var cfg = pm.Config; cfg.DefaultProfile = "work"; ConfigStore.Save(home.Paths, cfg);
        env.User["CLAUDE_CONFIG_DIR"] = home.Paths.ProfileDir("work").ToUpperInvariant() + "\\";
        var s = pm.Status();
        Assert.True(s.EnvMatchesDefault);
        Assert.Equal("work", s.DefaultProfile);
    }

    [Fact]
    public void Own_Dir_NestedJunction_RecreatedNotFollowed()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        var outside = Path.Combine(home.Dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "big.md"), "BIG");
        var src = Path.Combine(home.Paths.DefaultHomeDir, "skills");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.md"), "A");
        Junction.Create(Path.Combine(src, "ext"), outside);
        pm.Init();
        pm.Add("work");
        var link = Path.Combine(home.Paths.ProfileDir("work"), "skills");

        pm.Own("work", "skills");

        var nested = Path.Combine(link, "ext");
        Assert.Equal("A", File.ReadAllText(Path.Combine(link, "a.md")));
        Assert.NotNull(Junction.GetTarget(nested)); // recreated as junction, not a copied tree
        Assert.Equal(Junction.GetTarget(Path.Combine(src, "ext")), Junction.GetTarget(nested));
        Assert.False(Directory.Exists(link + ".tmp-copy"));
        Assert.Equal("BIG", File.ReadAllText(Path.Combine(outside, "big.md")));
    }

    [Fact]
    public void Add_ReturnsSyncReport()
    {
        var (pm, home, _) = Make();
        using var _h = home;
        Directory.CreateDirectory(Path.Combine(home.Paths.DefaultHomeDir, "skills"));
        pm.Init();
        var report = pm.Add("work");
        Assert.NotNull(report);
        Assert.Empty(report.Problems);
        Assert.NotNull(pm.Share("work", "skills"));
    }
}
