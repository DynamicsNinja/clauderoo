using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliSharingTests
{
    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => new DateTime(2026, 11, 18));
        pm.Init();
        pm.Add("work");
        var skills = Path.Combine(home.Paths.DefaultHomeDir, "skills");
        Directory.CreateDirectory(Path.Combine(skills, "a"));
        Directory.CreateDirectory(Path.Combine(skills, "b"));
        return (pm, home);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Exclude_Include_RoundTrip_AndListShowsIt()
    {
        var (pm, home) = Make();
        using var _ = home;

        var r = Run(pm, "exclude", "work", "skills/a");
        Assert.Equal(0, r.code);
        Assert.Contains("work now leaves out skills/a.", r.o);
        Assert.Contains("excludes: skills/a", Run(pm, "list").o);
        Assert.Equal("skills/a", Run(pm, "exclude", "work").o.Trim());

        using (var doc = JsonDocument.Parse(Run(pm, "list", "--json").o))
        {
            var work = doc.RootElement.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "work");
            Assert.Equal("skills/a", work.GetProperty("exclude")[0].GetString());
        }

        r = Run(pm, "include", "work", "skills/a");
        Assert.Equal(0, r.code);
        Assert.Contains("work gets everything", Run(pm, "exclude", "work").o);
    }

    [Fact]
    public void Exclude_Missing_WarnsButWorks()
    {
        var (pm, home) = Make();
        using var _ = home;
        var r = Run(pm, "exclude", "work", "skills/ghost");
        Assert.Equal(0, r.code);
        Assert.Contains("isn't in", r.o);
    }

    [Fact]
    public void Exclude_BadSpec_Exit1()
    {
        var (pm, home) = Make();
        using var _ = home;
        var r = Run(pm, "exclude", "work", "settings.json/x");
        Assert.Equal(1, r.code);
        Assert.Contains("single file", r.e);
        Assert.Equal(1, Run(pm, "include", "work", "skills/a").code);
    }

    [Fact]
    public void Shared_AddListRemove()
    {
        var (pm, home) = Make();
        using var _ = home;
        Directory.CreateDirectory(Path.Combine(home.Paths.DefaultHomeDir, "rules"));

        Assert.Equal(0, Run(pm, "shared", "add", "rules").code);
        var list = Run(pm, "shared").o;
        Assert.Contains("rules", list);
        Assert.Contains("added", list);

        using (var doc = JsonDocument.Parse(Run(pm, "shared", "list", "--json").o))
        {
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            var rules = items.Single(i => i.GetProperty("name").GetString() == "rules");
            Assert.Equal("folder", rules.GetProperty("kind").GetString());
            Assert.False(rules.GetProperty("builtIn").GetBoolean());
            Assert.Contains(items, i => i.GetProperty("name").GetString() == "mcpServers");
        }

        Assert.Equal(1, Run(pm, "shared", "remove", "skills").code);
        Assert.Equal(1, Run(pm, "shared", "add", "projects", "--dir").code);
        Assert.Equal(1, Run(pm, "shared", "add", "--json", "x").code);
        Assert.Equal(0, Run(pm, "shared", "remove", "rules").code);
        Assert.DoesNotContain("rules", Run(pm, "shared").o);
    }
}
