namespace Clauderoo.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        using var home = new TempHome();
        Assert.Null(ConfigStore.Load(home.Paths));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var home = new TempHome();
        var cfg = new ClauderooConfig
        {
            DefaultProfile = "personal",
            HomeDir = home.Paths.DefaultHomeDir,
            HomeJson = home.Paths.DefaultHomeJson,
            Profiles =
            {
                ["personal"] = new ProfileEntry { Home = true },
                ["work"] = new ProfileEntry { Overrides = ["settings.json"], StartDir = @"C:\x" },
            },
        };

        ConfigStore.Save(home.Paths, cfg);
        var loaded = ConfigStore.Load(home.Paths);

        Assert.NotNull(loaded);
        Assert.Equal("personal", loaded.DefaultProfile);
        Assert.Equal("wt", loaded.Terminal);
        Assert.Equal(cfg.HomeDir, loaded.HomeDir);
        Assert.Equal(cfg.HomeJson, loaded.HomeJson);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.True(loaded.Profiles["personal"].Home);
        Assert.Null(loaded.Profiles["personal"].StartDir);
        Assert.Equal(["settings.json"], loaded.Profiles["work"].Overrides);
        Assert.Equal(@"C:\x", loaded.Profiles["work"].StartDir);
        Assert.Equal("personal", loaded.HomeProfileName);

        var raw = File.ReadAllText(home.Paths.ConfigFile);
        Assert.Contains("\"defaultProfile\"", raw);
        Assert.Contains("\"overrides\"", raw);
        Assert.False(File.Exists(home.Paths.ConfigFile + ".tmp"));
    }

    [Fact]
    public void Load_CorruptJson_ThrowsClauderooException()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(home.Paths.Root);
        File.WriteAllText(home.Paths.ConfigFile, "{ not json");
        var ex = Assert.Throws<ClauderooException>(() => ConfigStore.Load(home.Paths));
        Assert.Equal("config.json is not valid JSON: " + home.Paths.ConfigFile, ex.Message);
    }
}
