namespace Clauderoo.Core.Tests;

public class RenameTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome h, FakeEnv env) Make(ISecretStore? secrets = null)
    {
        var h = new TempHome();
        var env = new FakeEnv();
        var pm = new ProfileManager(h.Paths, env, () => Now, secrets ?? new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, h, env);
    }

    private static void WritePluginRecord(TempHome h, string file, string content)
    {
        var dir = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void Rename_Plain_FolderConfigMoved()
    {
        var h = new TempHome(); using var _h = h;
        var plugins = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "keep.txt"), "x");
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("work"), "plugins")));
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt"), "m");

        pm.Rename("work", "job");

        Assert.False(Directory.Exists(h.Paths.ProfileDir("work")));
        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("job"), "marker.txt")));
        var cfg = pm.Config;
        Assert.True(cfg.Profiles.ContainsKey("job"));
        Assert.False(cfg.Profiles.ContainsKey("work"));
        // junctions moved as links; home contents intact
        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("job"), "plugins")));
        Assert.True(File.Exists(Path.Combine(plugins, "keep.txt")));
    }

    [Fact]
    public void Rename_PluginRecords_Refused_ForceAllows()
    {
        var (pm, h, _) = Make(); using var _h = h;
        var dir = h.Paths.ProfileDir("work");
        WritePluginRecord(h, "installed_plugins.json",
            "{\"plugins\":{\"x\":[{\"installPath\":\"" + Json(dir) + "\\\\plugins\\\\cache\\\\x\"}]}}");
        var ex = Assert.Throws<ClauderooException>(() => pm.Rename("work", "job"));
        Assert.Equal(
            "work's folder is referenced by shared plugin records (installed from the work profile). " +
            $"Reinstall those plugins from {h.Paths.DefaultHomeDir}, or run 'clauderoo rename work job --force'.",
            ex.Message);
        Assert.True(Directory.Exists(dir));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));

        pm.Rename("work", "job", force: true);
        Assert.True(Directory.Exists(h.Paths.ProfileDir("job")));
        Assert.True(pm.Config.Profiles.ContainsKey("job"));
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("_removed")]
    [InlineData("")]
    public void Rename_InvalidName_Throws(string bad)
    {
        var (pm, h, _) = Make(); using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.Rename("work", bad));
        Assert.True(Directory.Exists(h.Paths.ProfileDir("work")));
    }

    [Fact]
    public void Rename_Duplicate_Throws()
    {
        var (pm, h, _) = Make(); using var _h = h;
        var ex = Assert.Throws<ClauderooException>(() => pm.Rename("work", "personal"));
        Assert.Equal("A profile named 'personal' already exists.", ex.Message);
        pm.Add("other");
        ex = Assert.Throws<ClauderooException>(() => pm.Rename("work", "other"));
        Assert.Equal("A profile named 'other' already exists.", ex.Message);
    }

    [Fact]
    public void Rename_SameName_NoOp()
    {
        var (pm, h, _) = Make(); using var _h = h;
        var r = pm.Rename("work", "work");
        Assert.Empty(r.Problems);
        Assert.True(Directory.Exists(h.Paths.ProfileDir("work")));
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Rename_Unknown_Throws()
    {
        var (pm, h, _) = Make(); using var _h = h;
        var ex = Assert.Throws<ClauderooException>(() => pm.Rename("nope", "job"));
        Assert.Contains("Unknown profile 'nope'", ex.Message);
    }

    [Fact]
    public void Rename_MoveFails_NothingChanged()
    {
        var (pm, h, env) = Make(); using var _h = h;
        pm.SetDefault("work");
        var before = File.ReadAllText(h.Paths.ConfigFile);
        var envBefore = env.User["CLAUDE_CONFIG_DIR"];
        var locked = Path.Combine(h.Paths.ProfileDir("work"), "busy.txt");
        File.WriteAllText(locked, "x");
        using var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Assert.Throws<ClauderooException>(() => pm.Rename("work", "job"));

        Assert.Equal("Couldn't rename work's folder — close work's Claude sessions and try again.", ex.Message);
        Assert.Equal(before, File.ReadAllText(h.Paths.ConfigFile));
        Assert.Equal(envBefore, env.User["CLAUDE_CONFIG_DIR"]);
        Assert.True(Directory.Exists(h.Paths.ProfileDir("work")));
        Assert.False(Directory.Exists(h.Paths.ProfileDir("job")));
    }

    [Fact]
    public void Rename_ConfigSaveFails_RolledBack()
    {
        var (pm, h, env) = Make(); using var _h = h;
        pm.SetDefault("work");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt"), "m");
        var before = File.ReadAllText(h.Paths.ConfigFile);
        var envBefore = env.User["CLAUDE_CONFIG_DIR"];
        pm.BeforeRenameCommit = () => throw new IOException("disk full");

        Assert.Throws<IOException>(() => pm.Rename("work", "job"));

        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt")));
        Assert.False(Directory.Exists(h.Paths.ProfileDir("job")));
        Assert.Equal(before, File.ReadAllText(h.Paths.ConfigFile));
        Assert.Equal(envBefore, env.User["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("work", pm.Config.DefaultProfile);
    }

    [Fact]
    public void Rename_ApiKey_HelperAndKeyUpdated()
    {
        var (pm, h, _) = Make(new DpapiSecretStore()); using var _h = h;
        pm.SetProvider("work", Providers.ApiKey, apiKey: "sk-test-1");

        pm.Rename("work", "job");

        var settings = File.ReadAllText(Path.Combine(h.Paths.ProfileDir("job"), "settings.json"));
        Assert.Contains("clauderoo.exe", settings);
        Assert.Contains("apikey job", settings);
        Assert.DoesNotContain("apikey work", settings);
        Assert.Equal("sk-test-1", pm.GetApiKey("job"));
    }

    [Fact]
    public void Rename_Default_EnvVarUpdated()
    {
        var (pm, h, env) = Make(); using var _h = h;
        pm.SetDefault("work");

        pm.Rename("work", "job");

        Assert.Equal("job", pm.Config.DefaultProfile);
        Assert.Equal(h.Paths.ProfileDir("job"), env.User["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void Rename_EnvVarElsewhere_Untouched()
    {
        var (pm, h, env) = Make(); using var _h = h;
        env.User["CLAUDE_CONFIG_DIR"] = @"C:\somewhere\else";

        pm.Rename("work", "job");

        Assert.Equal(@"C:\somewhere\else", env.User["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void Rename_Home_LabelOnly_HomeUntouched()
    {
        var (pm, h, env) = Make(); using var _h = h;
        var homeDir = h.Paths.DefaultHomeDir;
        File.WriteAllText(Path.Combine(homeDir, "settings.json"), "{\"a\":1}");
        List<string> Snap() => Directory.GetFileSystemEntries(homeDir, "*", SearchOption.AllDirectories)
            .OrderBy(x => x).Select(x => x + "|" + (File.Exists(x) ? File.ReadAllText(x) : "")).ToList();
        var snapshot = Snap();
        var homeJson = File.ReadAllText(h.Paths.DefaultHomeJson);

        var r = pm.Rename("personal", "main");

        Assert.Empty(r.Problems);
        var cfg = pm.Config;
        Assert.True(cfg.Profiles["main"].Home);
        Assert.False(cfg.Profiles.ContainsKey("personal"));
        Assert.Equal("main", cfg.DefaultProfile);
        Assert.Equal(homeDir, cfg.HomeDir);
        Assert.True(Directory.Exists(homeDir));
        Assert.False(Directory.Exists(h.Paths.ProfileDir("main")));
        Assert.Equal(snapshot, Snap());
        Assert.Equal(homeJson, File.ReadAllText(h.Paths.DefaultHomeJson));
        Assert.False(env.User.ContainsKey("CLAUDE_CONFIG_DIR"));
    }
}
