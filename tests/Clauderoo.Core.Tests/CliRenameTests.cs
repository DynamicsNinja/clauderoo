using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliRenameTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0);

    private static (ProfileManager pm, TempHome h) Make()
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, h);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Rename_Ok()
    {
        var (pm, h) = Make(); using var _h = h;
        var r = Run(pm, "rename", "work", "job");
        Assert.Equal(0, r.code);
        Assert.Contains("Renamed 'work' to 'job'.", r.o);
        Assert.True(pm.Config.Profiles.ContainsKey("job"));
        Assert.False(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Cli_Rename_Duplicate_Exit1()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Add("job");
        var r = Run(pm, "rename", "work", "job");
        Assert.Equal(1, r.code);
        Assert.Contains("A profile named 'job' already exists.", r.e);
    }

    [Fact]
    public void Cli_Rename_BadArgs_Usage()
    {
        var (pm, h) = Make(); using var _h = h;
        var r = Run(pm, "rename", "work");
        Assert.Equal(1, r.code);
        Assert.Contains("Usage: clauderoo rename <old> <new> [--force]", r.e);
    }

    [Fact]
    public void Cli_Rename_ForcePassed()
    {
        var (pm, h) = Make(); using var _h = h;
        var dir = h.Paths.ProfileDir("work");
        var plugins = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "installed_plugins.json"),
            "{\"plugins\":{\"x\":[{\"installPath\":\"" + dir.Replace("\\", "\\\\") + "\\\\plugins\\\\cache\\\\x\"}]}}");
        var first = Run(pm, "rename", "work", "job");
        Assert.Equal(1, first.code);
        Assert.Contains("shared plugin records", first.e);
        var r = Run(pm, "rename", "--force", "work", "job");
        Assert.Equal(0, r.code);
        Assert.True(pm.Config.Profiles.ContainsKey("job"));
    }
}
