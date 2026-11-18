using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class ScriptingTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, ReleaseHooks? hooks, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, hooks: hooks);
        return (code, o.ToString(), e.ToString());
    }

    // ---- completion ----

    private static string Help(ProfileManager pm) => Run(pm, null, "help").o;

    // The command named at the start of each help line ("  start [name] …" -> start).
    private static HashSet<string> HelpCommands(string help) =>
        Regex.Matches(help, @"^  ([a-z_][a-z0-9_-]*)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();

    // Every help line about a command, with its continuation lines.
    private static string HelpBlock(string help, string cmd)
    {
        var lines = help.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var block = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], $@"^  {Regex.Escape(cmd)}( |$)")) continue;
            block.Add(lines[i]);
            for (var j = i + 1; j < lines.Count && lines[j].StartsWith("      ", StringComparison.Ordinal); j++) block.Add(lines[j]);
        }
        return string.Join("\n", block);
    }

    [Fact]
    public void Completion_CommandList_MatchesDispatchAndHelp()
    {
        var (pm, home) = Make(); using var _h = home;
        var help = Help(pm);
        var visible = Completion.VisibleCommands.ToHashSet();
        var known = Commands.KnownCommands.ToHashSet();
        // Every command the help names is accepted, and every accepted command (but the alias and the hidden one) has help.
        Assert.Empty(HelpCommands(help).Except(known).Except(["help"]));
        Assert.Empty(known.Except(HelpCommands(help)).Except(["run", "__complete"]));
        // The completion data covers exactly the visible commands.
        Assert.Equal(visible.Except(["help"]).Order(), Completion.Words.Keys.Order());
        Assert.DoesNotContain("__complete", visible);
        Assert.DoesNotContain("__complete", help);
        Assert.All(Completion.TakesProfile, c => Assert.Contains(c, known));
    }

    [Fact]
    public void Completion_FlagsAreDocumented()
    {
        var (pm, home) = Make(); using var _h = home;
        var help = Help(pm);
        var jsonLine = HelpBlock(help, "--json");
        foreach (var (cmd, words) in Completion.Words)
        {
            var block = HelpBlock(help, cmd == "run" ? "start" : cmd);
            foreach (var w in words.Where(w => w.StartsWith("--", StringComparison.Ordinal)))
            {
                if (w == "--json" && jsonLine.Contains(cmd)) continue;
                Assert.True(block.Contains(w), $"'{cmd} {w}' is completed but not in the help for {cmd}");
            }
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public void Completion_Scripts_ListEveryCommandAndFlag(string shell)
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, null, "completion", shell);
        Assert.Equal(0, r.code);
        foreach (var c in Completion.VisibleCommands) Assert.Contains(c, r.o);
        foreach (var w in Completion.Words.Values.SelectMany(x => x)) Assert.Contains(w, r.o);
        Assert.Contains("__complete profiles", r.o);
        Assert.Contains(shell == "bash" ? "complete -F _clauderoo cr" : "Register-ArgumentCompleter -Native", r.o);
    }

    [Fact]
    public void Completion_BashCommandWord_IsExact()
    {
        var (pm, home) = Make(); using var _h = home;
        var bash = Run(pm, null, "completion", "bash").o;
        var m = Regex.Match(bash, "compgen -W \"([^\"$]+)\"");
        Assert.Equal(Completion.VisibleCommands.Order(), m.Groups[1].Value.Split(' ').Order());
    }

    [Fact]
    public void Completion_UnknownShell_Error()
    {
        var (pm, home) = Make(); using var _h = home;
        Assert.Equal(1, Run(pm, null, "completion", "fish").code);
    }

    [Fact]
    public void HiddenComplete_PrintsProfiles_WithoutSyncing()
    {
        var (pm, home) = Make(); using var _h = home;
        var stamp = File.GetLastWriteTimeUtc(home.Paths.ConfigFile);
        var r = Run(pm, null, "__complete", "profiles");
        Assert.Equal(0, r.code);
        Assert.Equal(["personal", "work"], r.o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order());
        Assert.Equal("", r.e);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(home.Paths.ConfigFile));
    }

    [Fact]
    public void HiddenComplete_NoConfig_PrintsNothing()
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        var r = Run(pm, null, "__complete", "profiles");
        Assert.Equal(0, r.code);
        Assert.Equal("", r.o + r.e);
    }

    // ---- env --json ----

    [Fact]
    public void EnvJson_KeepsNamesExactly()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.SetEnv("work", [new("ANTHROPIC_MODEL", "claude-x"), new("my_var", "a b")], []);
        var r = Run(pm, null, "env", "work", "--json");
        Assert.Equal(0, r.code);
        var j = JsonNode.Parse(r.o)!;
        Assert.Equal(1, j["schemaVersion"]!.GetValue<int>());
        Assert.Equal("work", j["profile"]!.GetValue<string>());
        Assert.Equal("claude-x", j["env"]!["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Equal("a b", j["env"]!["my_var"]!.GetValue<string>());
    }

    [Fact]
    public void EnvJson_WithSet_IsAnError()
    {
        var (pm, home) = Make(); using var _h = home;
        var r = Run(pm, null, "env", "work", "set", "A=1", "--json");
        Assert.Equal(1, r.code);
        Assert.Contains("\"error\"", r.e);
        Assert.Empty(pm.GetEnv("work"));
    }

    // ---- auto --schedule ----

    private sealed class FakeScheduler : ITaskScheduler
    {
        public bool Exists;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public (int exit, string output) Run(IReadOnlyList<string> args)
        {
            Calls.Add(args);
            switch (args[0])
            {
                case "/Query": return (Exists ? 0 : 1, Exists ? "" : "ERROR: not found");
                case "/Delete": Exists = false; return (0, "SUCCESS");
                default: Exists = true; return (0, "SUCCESS");
            }
        }
    }

    [Fact]
    public void Schedule_On_CreatesPerUserTask()
    {
        var (pm, home) = Make(); using var _h = home;
        var tray = Path.Combine(home.Dir, "my apps", "clauderoow.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tray)!);
        File.WriteAllText(tray, "x");
        var s = new FakeScheduler();
        var r = Run(pm, new ReleaseHooks { Scheduler = s, TrayExe = tray }, "auto", "--schedule", "on", "--every", "15");
        Assert.Equal(0, r.code);
        var args = Assert.Single(s.Calls);
        Assert.Equal(["/Create", "/TN", AutoSchedule.TaskName, "/TR", $"\"{tray}\" --auto-once", "/SC", "MINUTE", "/MO", "15", "/F"], args);
        Assert.DoesNotContain("/RU", args); // the current user, no stored password, no elevation
        Assert.DoesNotContain("/RL", args);
        Assert.Contains("every 15 minutes", r.o);
        Assert.Contains("auto-switch is off", r.e);
    }

    [Fact]
    public void Schedule_Off_RemovesOnlyWhenPresent()
    {
        var (pm, home) = Make(); using var _h = home;
        var s = new FakeScheduler();
        var r = Run(pm, new ReleaseHooks { Scheduler = s }, "auto", "--schedule", "off");
        Assert.Equal(0, r.code);
        Assert.Contains("no scheduled task", r.o);
        Assert.Single(s.Calls); // query only
        s.Exists = true;
        r = Run(pm, new ReleaseHooks { Scheduler = s }, "auto", "--schedule", "off");
        Assert.Contains("Removed", r.o);
        Assert.Equal(["/Delete", "/TN", AutoSchedule.TaskName, "/F"], s.Calls[^1]);
    }

    [Fact]
    public void Schedule_NoOnOff_SaysWhetherTheTaskExists()
    {
        var (pm, home) = Make(); using var _h = home;
        var s = new FakeScheduler();
        var r = Run(pm, new ReleaseHooks { Scheduler = s }, "auto", "--schedule");
        Assert.Equal(0, r.code);
        Assert.Contains("No scheduled task", r.o);
        s.Exists = true;
        r = Run(pm, new ReleaseHooks { Scheduler = s }, "auto", "--schedule");
        Assert.Contains("is set up", r.o);
        Assert.All(s.Calls, c => Assert.Equal("/Query", c[0])); // read-only
    }

    [Theory]
    [InlineData("--schedule", "--every", "5")]
    [InlineData("--schedule", "maybe")]
    [InlineData("--schedule", "on", "--every", "0")]
    [InlineData("--schedule", "on", "--every", "x")]
    [InlineData("--schedule", "on", "--threshold", "50")]
    public void Schedule_BadArgs(params string[] rest)
    {
        var (pm, home) = Make(); using var _h = home;
        var tray = Path.Combine(home.Dir, "clauderoow.exe");
        File.WriteAllText(tray, "x");
        var s = new FakeScheduler();
        var r = Run(pm, new ReleaseHooks { Scheduler = s, TrayExe = tray }, ["auto", .. rest]);
        Assert.Equal(1, r.code);
        Assert.DoesNotContain(s.Calls, c => c[0] == "/Create");
    }

    [Fact]
    public void Schedule_NoTrayExe_Refuses()
    {
        var (pm, home) = Make(); using var _h = home;
        var s = new FakeScheduler();
        var r = Run(pm, new ReleaseHooks { Scheduler = s, TrayExe = Path.Combine(home.Dir, "missing.exe") }, "auto", "--schedule", "on");
        Assert.Equal(1, r.code);
        Assert.Empty(s.Calls);
    }

    // ---- uninstall --purge ----

    private sealed class FakePath(string raw) : IUserPath
    {
        public string Raw = raw;
        public string Read() => Raw;
        public void Write(string raw) => Raw = raw;
    }

    private sealed class Rig : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "clauderoo-purge-" + Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "Local", "clauderoo");
        public string Data => Path.Combine(Root, "User", ".clauderoo");
        public string Shared => Path.Combine(Root, "User", ".claude", "plugins");
        public FakeEnv Env = new();
        public List<string> Scheduled = [];

        public Rig()
        {
            Directory.CreateDirectory(Install);
            File.WriteAllText(Path.Combine(Install, "cr.exe"), "x");
            Directory.CreateDirectory(Path.Combine(Data, "profiles", "work"));
            File.WriteAllText(Path.Combine(Data, "config.json"), "{}");
            File.WriteAllText(Path.Combine(Data, "profiles", "work", ".clauderoo-token"), "blob");
            Directory.CreateDirectory(Shared);
            File.WriteAllText(Path.Combine(Shared, "keep.txt"), "precious");
            // A profile's shared folder is a junction into home: purge must remove the link, never what it points to.
            Junction.Create(Path.Combine(Data, "profiles", "work", "plugins"), Shared);
        }

        public ReleaseHooks Hooks(string? answer) => new()
        {
            Env = Env, InstallDir = Install, ReadLine = () => answer,
            Uninstall = new UninstallOptions
            {
                Path = new FakePath(Install), SetAutoStart = _ => { }, Schedule = Scheduled.Add, DataDir = Data, IsTrayRunning = () => false,
            },
        };

        public void Dispose()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                    if (new DirectoryInfo(d).LinkTarget != null) Directory.Delete(d);
                Directory.Delete(Root, true);
            }
            catch (IOException) { }
        }
    }

    private static (int code, string o, string e) Uninstall(Rig rig, string? answer, params string[] args)
    {
        using var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now);
        return Run(pm, rig.Hooks(answer), ["uninstall", .. args]);
    }

    [Fact]
    public void Purge_Confirmed_DeletesData_KeepsJunctionTargets()
    {
        using var rig = new Rig();
        rig.Env.User["CLAUDE_CONFIG_DIR"] = Path.Combine(rig.Data, "profiles", "work");
        var r = Uninstall(rig, "delete", "--purge");
        Assert.Equal(0, r.code);
        Assert.False(Directory.Exists(rig.Data));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(rig.Shared, "keep.txt")));
        Assert.Null(rig.Env.GetUser("CLAUDE_CONFIG_DIR")); // it pointed into the deleted folder
        Assert.Single(rig.Scheduled);
        Assert.Contains("not touched", r.o);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public void Purge_NotConfirmed_ChangesNothing(string? answer)
    {
        using var rig = new Rig();
        var r = Uninstall(rig, answer, "--purge");
        Assert.Equal(1, r.code);
        Assert.True(File.Exists(Path.Combine(rig.Data, "config.json")));
        Assert.Empty(rig.Scheduled); // not even the program was uninstalled
        Assert.Contains("Nothing was changed", r.o);
    }

    [Fact]
    public void Purge_Yes_SkipsPrompt()
    {
        using var rig = new Rig();
        var r = Uninstall(rig, null, "--purge", "--yes");
        Assert.Equal(0, r.code);
        Assert.False(Directory.Exists(rig.Data));
        Assert.True(File.Exists(Path.Combine(rig.Shared, "keep.txt")));
    }

    [Fact]
    public void Purge_RefusesDataThatHoldsHome()
    {
        var root = Path.Combine(Path.GetTempPath(), "clauderoo-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            var e = Assert.Throws<ClauderooException>(() => Uninstaller.Purge(root, [Path.Combine(root, ".claude")]));
            Assert.Contains("never touched", e.Message);
            Assert.True(Directory.Exists(Path.Combine(root, ".claude")));
            Assert.Throws<ClauderooException>(() => Uninstaller.Purge(Path.Combine(root, ".claude", "x"), [Path.Combine(root, ".claude")]));
            Assert.Throws<ClauderooException>(() => Uninstaller.Purge(@"C:\", []));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Purge_RefusesDataFolderThatIsAJunction()
    {
        using var rig = new Rig();
        var link = Path.Combine(rig.Root, "linked-data");
        Junction.Create(link, rig.Data);
        Assert.Throws<ClauderooException>(() => Uninstaller.Purge(link, []));
        Assert.True(File.Exists(Path.Combine(rig.Data, "config.json")));
    }

    // ---- export of one profile (what the window's "Only <name>" exports) ----

    [Fact]
    public void Export_SingleProfile_OnlyThatOne()
    {
        var (pm, home) = Make(); using var _h = home;
        pm.Add("other");
        var json = JsonNode.Parse(pm.Export(["work"], includeEnv: false))!;
        var names = json["profiles"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["work"], names);
    }
}
