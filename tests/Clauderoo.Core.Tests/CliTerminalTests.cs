using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

/// <summary>cr config, cr wt-profiles and cr code.</summary>
public class CliTerminalTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);
    private const string Exe = @"C:\x\clauderoo.exe";

    private static (ProfileManager pm, TempHome home, FakeSecretStore tokens) Make()
    {
        var home = new TempHome();
        var tokens = new FakeSecretStore();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(), tokens)
        {
            HelperExe = Exe,
            WtFragmentFile = WtFragment.FileUnder(Path.Combine(home.Dir, "local")),
        };
        pm.Init();
        pm.Add("work");
        return (pm, home, tokens);
    }

    private static (int code, string o, string e, List<LaunchSpec> launched) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var launched = new List<LaunchSpec>();
        var code = Commands.Run(args, o, e, pm, s => { launched.Add(s); return 0; }, s => { launched.Add(s); return 7; },
            wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString(), launched);
    }

    // ---------- config ----------

    [Fact]
    public void Config_ShowsDefaults()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var r = Run(pm, "config");
        Assert.Equal(0, r.code);
        Assert.Contains("terminal: wt", r.o);
        Assert.Contains("shell:    cmd  (Command Prompt)", r.o);
    }

    [Fact]
    public void Config_SetsTerminalAndShell()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Assert.Equal(0, Run(pm, "config", "terminal", "conhost").code);
        Assert.Equal("conhost", pm.Config.Terminal);
        var r = Run(pm, "config", "shell", "pwsh", "terminal", "wt");
        Assert.Equal(0, r.code);
        Assert.Equal("pwsh", pm.Config.Shell);
        Assert.Equal("wt", pm.Config.Terminal);
        Assert.Contains("shell:    pwsh  (PowerShell 7)", r.o);
    }

    [Theory]
    [InlineData("terminal", "kitty")]
    [InlineData("shell", "bash")]
    [InlineData("shell")]
    [InlineData("colour", "red")]
    public void Config_Invalid_Exit1(params string[] rest)
    {
        var (pm, home, _) = Make(); using var _h = home;
        var r = Run(pm, ["config", .. rest]);
        Assert.Equal(1, r.code);
        Assert.Equal("wt", pm.Config.Terminal);
        Assert.Null(pm.Config.Shell);
    }

    [Fact]
    public void Config_Json()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Run(pm, "config", "shell", "powershell");
        var r = Run(pm, "config", "--json");
        Assert.Equal(0, r.code);
        var j = JsonNode.Parse(r.o)!;
        Assert.Equal("wt", (string)j["terminal"]!);
        Assert.Equal("powershell", (string)j["shell"]!);
    }

    [Fact]
    public void Config_Conhost_StartTabOpensConsoleWindow()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Run(pm, "config", "terminal", "conhost");
        var r = Run(pm, "start", "work", home.Dir, "--tab");
        Assert.Equal(0, r.code);
        var spec = Assert.Single(r.launched);
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal(new[] { "/c", "start", "Claude · work", "/D", home.Dir, "cmd", "/k" }, spec.Args.Take(7));
    }

    // ---------- wt-profiles ----------

    [Fact]
    public void WtProfiles_WritesRemovesAndFollowsProfileChanges()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var file = pm.WtFragmentFile!;
        var r = Run(pm, "wt-profiles");
        Assert.Equal(0, r.code);
        Assert.Contains("Added 2 profile(s) to Windows Terminal", r.o);
        var names = JsonNode.Parse(File.ReadAllText(file))!["profiles"]!.AsArray().Select(p => (string)p!["name"]!);
        Assert.Equal(new[] { "Claude · personal", "Claude · work" }, names);

        Assert.Equal(0, Run(pm, "add", "extra").code);
        Assert.Contains("Claude · extra", File.ReadAllText(file));
        Assert.Equal(0, Run(pm, "rename", "extra", "other").code);
        Assert.DoesNotContain("Claude · extra", File.ReadAllText(file));
        Assert.Contains("Claude · other", File.ReadAllText(file));
        Assert.Equal(0, Run(pm, "remove", "other").code);
        Assert.DoesNotContain("Claude · other", File.ReadAllText(file));
        Assert.Equal(0, Run(pm, "config", "shell", "pwsh").code);
        Assert.Contains("pwsh.exe -NoExit", File.ReadAllText(file));

        r = Run(pm, "wt-profiles", "--remove");
        Assert.Equal(0, r.code);
        Assert.False(File.Exists(file));
        Assert.Contains("Removed", r.o);
        Assert.Contains("no clauderoo profiles", Run(pm, "wt-profiles", "--remove").o);

        // Without a fragment, profile changes don't create one.
        Run(pm, "add", "later");
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void WtProfiles_BadArgs_Exit1()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Assert.Equal(1, Run(pm, "wt-profiles", "now").code);
        Assert.False(File.Exists(pm.WtFragmentFile));
    }

    // ---------- code ----------

    private static string FakeCode(TempHome home) =>
        Path.Combine(EditorLaunchTests.FakeVsCode(Path.Combine(home.Dir, "VS Code")).Bin, "code.cmd");

    [Fact]
    public void Code_OpensVsCodeWithTheProfile()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var (_, exe, js) = EditorLaunchTests.FakeVsCode(Path.Combine(home.Dir, "VS Code"));
        var dir = Path.Combine(home.Dir, "My Project");
        Directory.CreateDirectory(dir);
        var r = Run(pm, "code", "work", dir, "--editor", Path.Combine(home.Dir, "VS Code", "bin", "code.cmd"));
        Assert.Equal(0, r.code);
        var s = Assert.Single(r.launched);
        Assert.Equal(exe, s.FileName, ignoreCase: true);
        Assert.Equal(new[] { js, "--new-window", dir }, s.Args, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(home.Paths.ProfileDir("work"), s.ConfigDirEnv);
        Assert.Equal("work", s.ExtraEnv!["CLAUDEROO_PROFILE"]);
        Assert.Equal("1", s.ExtraEnv["ELECTRON_RUN_AS_NODE"]);
        Assert.Contains($"Opened {dir}", r.o);
        Assert.Contains("with work", r.o);
    }

    [Fact]
    public void Code_NoProfile_UsesFolderMappingThenDefault()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var editor = FakeCode(home);
        var mapped = Path.Combine(home.Dir, "mapped");
        Directory.CreateDirectory(mapped);
        pm.Map("work", FolderMap.Normalize(mapped));
        var r = Run(pm, "code", mapped, "--editor", editor);
        Assert.Equal(0, r.code);
        Assert.Contains("Using work (mapped from", r.o);
        Assert.Equal(home.Paths.ProfileDir("work"), Assert.Single(r.launched).ConfigDirEnv);

        var other = Path.Combine(home.Dir, "other");
        Directory.CreateDirectory(other);
        r = Run(pm, "code", other, "--editor", editor);
        Assert.Equal(0, r.code);
        Assert.Equal("personal", Assert.Single(r.launched).ExtraEnv!["CLAUDEROO_PROFILE"]);
        Assert.Null(r.launched[0].ConfigDirEnv); // home profile: no CLAUDE_CONFIG_DIR
    }

    [Fact]
    public void Code_TokenProfile_GetsItsToken()
    {
        var (pm, home, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: "sk-ant-oat01-SECRET");
        var r = Run(pm, "code", "work", home.Dir, "--editor", FakeCode(home));
        Assert.Equal(0, r.code);
        Assert.Equal("sk-ant-oat01-SECRET", Assert.Single(r.launched).ExtraEnv![ProfileManager.TokenEnvVar]);
        Assert.DoesNotContain("SECRET", r.o + r.e);
        Assert.DoesNotContain(r.launched[0].Args, a => a.Contains("SECRET"));
    }

    [Fact]
    public void Code_Errors()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var editor = FakeCode(home);
        Assert.Contains("Unknown profile", Run(pm, "code", "nope", home.Dir, "--editor", editor).e);
        Assert.Contains("Folder not found", Run(pm, "code", "work", Path.Combine(home.Dir, "missing"), "--editor", editor).e);
        Assert.Equal(1, Run(pm, "code", "work", "--editor").code);
        Assert.Equal(1, Run(pm, "code", "work", "--cursor", "--editor", editor).code);
        Assert.Equal(1, Run(pm, "code", "work", home.Dir, "--", "x").code);
        Assert.Equal(1, Run(pm, "code", "work", home.Dir, "--bogus").code);
        var missing = Run(pm, "code", "work", home.Dir, "--editor", Path.Combine(home.Dir, "nothing", "code.cmd"));
        Assert.Equal(1, missing.code);
        Assert.Contains("was not found", missing.e);
    }
}
