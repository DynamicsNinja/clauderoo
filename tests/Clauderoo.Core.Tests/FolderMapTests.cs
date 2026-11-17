namespace Clauderoo.Core.Tests;

public class FolderMapTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        pm.Init("personal");
        pm.Add("work");
        return (pm, home);
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Map("personal", @"C:\src");
        pm.Map("work", @"C:\src\company");
        var r = FolderMap.Resolve(pm.Config, @"C:\src\company\app");
        Assert.Equal("work", r!.Value.Profile);
        Assert.Equal(@"C:\src\company", r.Value.MappedDir);
    }

    [Fact]
    public void Resolve_CaseAndTrailingSlashInsensitive()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Map("personal", @"C:\src");
        pm.Map("work", @"C:\src\company");
        Assert.Equal("work", FolderMap.Resolve(pm.Config, @"c:\SRC\Company\")!.Value.Profile);
    }

    [Fact]
    public void Resolve_SegmentBoundary()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Map("work", @"C:\src\company");
        Assert.Null(FolderMap.Resolve(pm.Config, @"C:\src\companyX"));
    }

    [Fact]
    public void Map_UnknownProfile_Throws()
    {
        var (pm, home) = Make();
        using var _h = home;
        Assert.Throws<ClauderooException>(() => pm.Map("nope", @"C:\src"));
    }

    [Fact]
    public void Rename_RepointsMappings()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Map("work", @"C:\src\company");
        pm.Rename("work", "job");
        Assert.Equal("job", Assert.Single(pm.Maps()).Profile);
    }

    [Fact]
    public void Remove_DeletesMappings()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Map("work", @"C:\src\company");
        pm.Remove("work");
        Assert.Empty(pm.Maps());
    }
}
