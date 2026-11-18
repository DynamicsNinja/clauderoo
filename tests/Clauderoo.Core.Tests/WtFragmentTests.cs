using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

public class WtFragmentTests
{
    private const string Exe = @"C:\Program Files\clauderoo\clauderoo.exe";

    private static ClauderooConfig Cfg(string? shell = null) => new()
    {
        Shell = shell,
        Profiles = new()
        {
            ["personal"] = new ProfileEntry { Home = true },
            ["work"] = new ProfileEntry { StartDir = @"C:\work stuff" },
        },
    };

    private static JsonArray Profiles(string json) => JsonNode.Parse(json)!["profiles"]!.AsArray();

    [Fact]
    public void Build_OneProfilePerClauderooProfile()
    {
        using var home = new TempHome();
        var list = Profiles(WtFragment.Build(Cfg(), home.Paths, Exe));
        Assert.Equal(2, list.Count);
        var personal = list[0]!;
        var work = list[1]!;
        Assert.Equal("Claude · personal", (string)personal["name"]!);
        Assert.Equal("Claude · work", (string)work["name"]!);
        Assert.Equal("Claude · work", (string)work["tabTitle"]!);
        Assert.Equal(@"C:\work stuff", (string)work["startingDirectory"]!);
        Assert.Null(personal["startingDirectory"]); // Windows Terminal's own default
        Assert.Equal(Launcher.ProfileCommandLine(Cfg(), home.Paths, "work", Exe), (string)work["commandline"]!);
        Assert.Contains($"set CLAUDE_CONFIG_DIR={home.Paths.ProfileDir("work")}&&", (string)work["commandline"]!);
        Assert.Contains($"\"{Exe}\" run work", (string)work["commandline"]!);
        Assert.Matches("^#[0-9A-F]{6}$", (string)work["tabColor"]!);
        Assert.Equal(WtFragment.ProfileGuid("work").ToString("B"), (string)work["guid"]!);
    }

    [Fact]
    public void Build_UsesConfiguredShell()
    {
        using var home = new TempHome();
        var work = Profiles(WtFragment.Build(Cfg("pwsh"), home.Paths, Exe))[1]!;
        Assert.StartsWith("pwsh.exe -NoExit -NoLogo -EncodedCommand ", (string)work["commandline"]!);
    }

    [Fact]
    public void TabColorAndGuid_StablePerName()
    {
        Assert.Equal(WtFragment.TabColor("work"), WtFragment.TabColor("work"));
        Assert.Equal(WtFragment.ProfileGuid("work"), WtFragment.ProfileGuid("work"));
        Assert.NotEqual(WtFragment.ProfileGuid("work"), WtFragment.ProfileGuid("personal"));
        var g = WtFragment.ProfileGuid("work").ToString("D");
        Assert.Equal('5', g[14]); // version 5 (name-based)
        Assert.Contains(g[19], "89ab");
        // Different names spread over the palette.
        var colors = Enumerable.Range(0, 40).Select(i => WtFragment.TabColor("p" + i)).Distinct().Count();
        Assert.True(colors > 3);
    }

    [Fact]
    public void WriteAndRemove()
    {
        using var home = new TempHome();
        var file = WtFragment.FileUnder(Path.Combine(home.Dir, "local"));
        Assert.EndsWith(@"Microsoft\Windows Terminal\Fragments\clauderoo\clauderoo.json", file);
        Assert.Equal(2, WtFragment.Write(file, Cfg(), home.Paths, Exe));
        Assert.Equal(WtFragment.Build(Cfg(), home.Paths, Exe), File.ReadAllText(file));
        Assert.True(WtFragment.Remove(file));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
        Assert.False(WtFragment.Remove(file));
    }

    [Fact]
    public void RefreshIfPresent_OnlyRewritesAnExistingFragment()
    {
        using var home = new TempHome();
        var file = WtFragment.FileUnder(Path.Combine(home.Dir, "local"));
        Assert.False(WtFragment.RefreshIfPresent(file, Cfg(), home.Paths, Exe));
        Assert.False(File.Exists(file)); // never created unasked
        Assert.False(WtFragment.RefreshIfPresent(null, Cfg(), home.Paths, Exe));

        WtFragment.Write(file, Cfg(), home.Paths, Exe);
        Assert.False(WtFragment.RefreshIfPresent(file, Cfg(), home.Paths, Exe)); // already up to date
        var cfg = Cfg();
        cfg.Profiles["new"] = new ProfileEntry();
        Assert.True(WtFragment.RefreshIfPresent(file, cfg, home.Paths, Exe));
        Assert.Equal(3, Profiles(File.ReadAllText(file)).Count);
    }

    [Fact]
    public void ProfileManager_SyncAndSettings_KeepTheFragmentInStep()
    {
        using var home = new TempHome();
        var file = WtFragment.FileUnder(Path.Combine(home.Dir, "local"));
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now) { HelperExe = Exe, WtFragmentFile = file };
        pm.Init();
        pm.Add("work");
        pm.SyncAll();
        Assert.False(File.Exists(file)); // no fragment until asked for

        WtFragment.Write(file, pm.Config, pm.Paths, Exe);
        pm.Add("extra");
        pm.SyncAll();
        Assert.Contains("Claude · extra", File.ReadAllText(file));

        pm.SetLaunchSettings(null, "powershell");
        Assert.Contains("powershell.exe -NoExit", File.ReadAllText(file));
    }

    [Fact]
    public void SetLaunchSettings_Validates()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        Assert.Throws<ClauderooException>(() => pm.SetLaunchSettings("kitty", null));
        Assert.Throws<ClauderooException>(() => pm.SetLaunchSettings(null, "bash"));
        pm.SetLaunchSettings("conhost", "pwsh");
        Assert.Equal("conhost", pm.Config.Terminal);
        Assert.Equal("pwsh", pm.Config.Shell);
        pm.SetLaunchSettings("wt", null);
        Assert.Equal("wt", pm.Config.Terminal);
        Assert.Equal("pwsh", pm.Config.Shell);
    }

    [Fact]
    public void Config_ShellNotWrittenUntilSet()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        Assert.DoesNotContain("\"shell\"", File.ReadAllText(home.Paths.ConfigFile));
        pm.SetLaunchSettings(null, "pwsh");
        Assert.Contains("\"shell\": \"pwsh\"", File.ReadAllText(home.Paths.ConfigFile));
    }
}
