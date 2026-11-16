namespace Clauderoo.Core.Tests;

public class SyncerDirTests
{
    private static (ClauderooConfig cfg, Syncer syncer, TempHome h) Setup(Action<ClauderooConfig>? tweak = null)
    {
        var h = new TempHome();
        var cfg = new ClauderooConfig
        {
            HomeDir = h.Paths.DefaultHomeDir,
            HomeJson = h.Paths.DefaultHomeJson,
            Profiles =
            {
                ["personal"] = new ProfileEntry { Home = true },
                ["work"] = new ProfileEntry(),
            },
        };
        tweak?.Invoke(cfg);
        return (cfg, new Syncer(cfg, h.Paths, () => new DateTime(2026, 1, 1)), h);
    }

    [Fact]
    public void Sync_CreatesJunctionsForExistingSourceDirs()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "plugins"));
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "skills"));

        var report = syncer.Sync("work");

        var dir = h.Paths.ProfileDir("work");
        Assert.Equal(Path.Combine(cfg.HomeDir, "plugins"), Junction.GetTarget(Path.Combine(dir, "plugins")));
        Assert.Equal(Path.Combine(cfg.HomeDir, "skills"), Junction.GetTarget(Path.Combine(dir, "skills")));
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void Sync_MissingSourceDir_SkippedThenLinkedLater()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        var link = Path.Combine(h.Paths.ProfileDir("work"), "agents");

        syncer.Sync("work");
        Assert.False(Directory.Exists(link));

        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "agents"));
        syncer.Sync("work");
        Assert.NotNull(Junction.GetTarget(link));
    }

    [Fact]
    public void Sync_RealDirAtJunctionPath_LeftAlone_Reported()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "skills"));
        var mine = Path.Combine(h.Paths.ProfileDir("work"), "skills", "mine.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "x");

        var report = syncer.Sync("work");

        Assert.True(File.Exists(mine));
        var problem = Assert.Single(report.Problems);
        Assert.Contains("skills", problem);
        Assert.Equal("work: skills is a real folder, not shared (run 'clauderoo share work skills' to share it)", problem);
    }

    [Fact]
    public void Sync_OverriddenDir_NotTouched()
    {
        var (cfg, syncer, h) = Setup(c => c.Profiles["work"].Overrides.Add("plugins"));
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "plugins"));

        syncer.Sync("work");

        Assert.False(Directory.Exists(Path.Combine(h.Paths.ProfileDir("work"), "plugins")));
    }

    [Fact]
    public void Sync_HomeProfile_NoOp()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "plugins"));

        var report = syncer.Sync("personal");

        Assert.False(Directory.Exists(h.Paths.ProfileDir("personal")));
        Assert.Empty(report.Problems);
        Assert.Empty(report.Notices);
    }

    [Fact]
    public void Sync_JunctionPointingElsewhere_Reported_NotRepointed()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        var other = Path.Combine(h.Dir, "other");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "hooks"));
        var link = Path.Combine(h.Paths.ProfileDir("work"), "hooks");
        Directory.CreateDirectory(h.Paths.ProfileDir("work"));
        Junction.Create(link, other);

        var report = syncer.Sync("work");

        Assert.Contains("hooks", Assert.Single(report.Problems));
        Assert.Equal(other, Junction.GetTarget(link));
    }

    [Fact]
    public void SyncAll_SyncsNonHomeProfiles()
    {
        var (cfg, syncer, h) = Setup();
        using var _ = h;
        Directory.CreateDirectory(Path.Combine(cfg.HomeDir, "commands"));

        syncer.SyncAll();

        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("work"), "commands")));
        Assert.False(Directory.Exists(h.Paths.ProfileDir("personal")));
    }
}
