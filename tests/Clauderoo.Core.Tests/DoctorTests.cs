using System.Text.Json.Nodes;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class DoctorTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    private sealed class Ctx : IDisposable
    {
        public TempHome Home = new();
        public FakeEnv Env = new();
        public ProfileManager Pm = null!;
        public string Install = null!;
        public string UserPath = "";
        public bool Wt = true;
        public string? Claude = @"C:\fake\claude.exe";
        public string? Gcloud = @"C:\fake\gcloud.cmd";
        public DoctorOptions Options => new()
        {
            FindClaude = () => Claude,
            ClaudeVersion = (_, _) => Task.FromResult<string?>("2.1.300 (Claude Code)"),
            WtAvailable = () => Wt,
            FindGcloud = () => Gcloud,
            InstallDir = Install,
            UserPath = () => UserPath,
        };
        public void Dispose() => Home.Dispose();
    }

    private static Ctx Make(bool init = true, bool installed = true)
    {
        var c = new Ctx();
        c.Install = Path.Combine(c.Home.Dir, "Local", "clauderoo");
        if (installed)
        {
            Directory.CreateDirectory(c.Install);
            File.WriteAllText(Path.Combine(c.Install, "cr.exe"), "x");
            File.WriteAllText(Path.Combine(c.Install, "clauderoo.exe"), "x");
            c.UserPath = @"C:\bin;" + c.Install;
        }
        Directory.CreateDirectory(Path.Combine(c.Home.Paths.DefaultHomeDir, "plugins"));
        c.Pm = new ProfileManager(c.Home.Paths, c.Env, () => Now, new FakeSecretStore(), new FakeGcloud()) { HelperExe = @"C:\x\clauderoo.exe" };
        if (init)
        {
            c.Pm.Init();
            c.Pm.Add("work");
        }
        return c;
    }

    private static Task<IReadOnlyList<CheckItem>> Run(Ctx c, IProgress<CheckItem>? p = null) =>
        Doctor.RunAsync(c.Home.Paths, c.Env, c.Options, default, p);

    private static CheckItem Row(IReadOnlyList<CheckItem> items, string key) => items.Single(i => i.Key == key);

    private static void RegisterMcp(Ctx c, string? command = null)
    {
        File.WriteAllText(c.Home.Paths.DefaultHomeJson, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["clauderoo"] = new JsonObject { ["type"] = "stdio", ["command"] = command ?? Path.Combine(c.Install, "cr.exe"), ["args"] = new JsonArray("mcp") },
            },
        }.ToJsonString());
    }

    [Fact]
    public async Task AllGood()
    {
        using var c = Make();
        RegisterMcp(c);
        var items = await Run(c);
        Assert.Equal(["config", "claude", "wt", "install", "path", "home", "mcp", "default", "links:work"], items.Select(i => i.Key));
        Assert.All(items, i => Assert.Equal(CheckStatus.Ok, i.Status));
        Assert.Equal("Claude Code 2.1.300 (Claude Code)", Row(items, "claude").Label);
    }

    [Fact]
    public async Task ReportsPendingFirst_ThenOneResultPerRow()
    {
        using var c = Make();
        var reports = new List<CheckItem>();
        var items = await Run(c, new SyncProgress(reports));
        Assert.All(reports.Take(items.Count), r => Assert.Equal(CheckStatus.Pending, r.Status));
        Assert.Equal(items.Count * 2, reports.Count);
        Assert.Equal(items.Select(i => i.Key).Order(), reports.Skip(items.Count).Select(r => r.Key).Order());
    }

    [Fact]
    public async Task NotInitialised_FailsConfig_SkipsProfileRows()
    {
        using var c = Make(init: false);
        var items = await Run(c);
        Assert.Equal(CheckStatus.Fail, Row(items, "config").Status);
        Assert.Contains("cr init", Row(items, "config").Hint);
        Assert.DoesNotContain(items, i => i.Key is "home" or "mcp" or "default");
    }

    [Fact]
    public async Task MissingTools_AndNotInstalled()
    {
        using var c = Make(installed: false);
        c.Claude = null;
        c.Wt = false;
        var items = await Run(c);
        Assert.Equal(CheckStatus.Fail, Row(items, "claude").Status);
        Assert.Equal(CheckStatus.Warn, Row(items, "wt").Status);
        Assert.Equal(CheckStatus.Warn, Row(items, "install").Status);
        Assert.Equal(CheckStatus.Warn, Row(items, "path").Status);
        Assert.Equal(CheckStatus.Warn, Row(items, "mcp").Status);
        Assert.DoesNotContain(items, i => i.Key == "gcloud"); // no Vertex profile
    }

    [Fact]
    public async Task Gcloud_OnlyWithVertexProfile()
    {
        using var c = Make();
        c.Pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "p", Region = "r" });
        c.Gcloud = null;
        Assert.Equal(CheckStatus.Fail, Row(await Run(c), "gcloud").Status);
    }

    [Fact]
    public async Task Mcp_PointsToMissingExe_Warns()
    {
        using var c = Make();
        RegisterMcp(c, Path.Combine(c.Home.Dir, "gone", "cr.exe"));
        var mcp = Row(await Run(c), "mcp");
        Assert.Equal(CheckStatus.Warn, mcp.Status);
        Assert.Contains("missing", mcp.Label);
    }

    [Fact]
    public async Task DanglingJunction_Fails()
    {
        using var c = Make();
        var link = Path.Combine(c.Home.Paths.ProfileDir("work"), "plugins");
        Assert.NotNull(Junction.GetTarget(link)); // the sync linked it to home
        Directory.Move(Path.Combine(c.Home.Paths.DefaultHomeDir, "plugins"), Path.Combine(c.Home.Paths.DefaultHomeDir, "plugins-moved"));
        var row = Row(await Run(c), "links:work");
        Assert.Equal(CheckStatus.Fail, row.Status);
        Assert.Contains("plugins", row.Label);
        Assert.True(Directory.Exists(Path.Combine(c.Home.Paths.DefaultHomeDir, "plugins-moved"))); // nothing was touched
    }

    [Fact]
    public async Task Default_Mismatch_Warns_UnknownFails()
    {
        using var c = Make();
        c.Pm.SetDefault("work");
        c.Env.User.Remove("CLAUDE_CONFIG_DIR");
        Assert.Equal(CheckStatus.Warn, Row(await Run(c), "default").Status);

        var cfg = c.Pm.Config;
        cfg.DefaultProfile = "ghost";
        ConfigStore.Save(c.Home.Paths, cfg);
        Assert.Equal(CheckStatus.Fail, Row(await Run(c), "default").Status);
    }

    [Fact]
    public async Task HomeDirMissing_Fails()
    {
        using var c = Make();
        var cfg = c.Pm.Config;
        cfg.HomeDir = Path.Combine(c.Home.Dir, "nope");
        ConfigStore.Save(c.Home.Paths, cfg);
        Assert.Equal(CheckStatus.Fail, Row(await Run(c), "home").Status);
    }

    [Fact]
    public async Task ChangesNothing()
    {
        using var c = Make();
        RegisterMcp(c);
        var before = Snapshot(c.Home.Dir);
        await Run(c);
        Assert.Equal(before, Snapshot(c.Home.Dir));
    }

    private static string Snapshot(string dir) => string.Join("\n",
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order()
            .Select(f => f + "|" + File.GetLastWriteTimeUtc(f).Ticks + "|" + new FileInfo(f).Length));

    // ---- CLI ----

    private static (int code, string o, string e) Cli(Ctx c, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, c.Pm, _ => 0, _ => 0, hooks: new ReleaseHooks { Doctor = c.Options });
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Json()
    {
        using var c = Make();
        RegisterMcp(c);
        var r = Cli(c, "doctor", "--json");
        Assert.Equal(0, r.code);
        var j = JsonNode.Parse(r.o)!;
        Assert.Equal(1, j["schemaVersion"]!.GetValue<int>());
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.Contains(j["items"]!.AsArray(), i => i!["key"]!.GetValue<string>() == "links:work");
    }

    [Fact]
    public void Cli_Text_FailureExits1()
    {
        using var c = Make();
        c.Claude = null;
        var r = Cli(c, "doctor");
        Assert.Equal(1, r.code);
        Assert.Contains("✗ Claude Code isn't on PATH", r.o);
        Assert.Contains("✓ Windows Terminal installed", r.o); // each line printed as its check finishes
    }

    [Fact]
    public void Cli_WorksWithoutInit()
    {
        using var c = Make(init: false);
        var r = Cli(c, "doctor");
        Assert.Equal(1, r.code);
        Assert.Contains("isn't set up yet", r.o);
        Assert.False(File.Exists(c.Home.Paths.ConfigFile));
    }

    private sealed class SyncProgress(List<CheckItem> into) : IProgress<CheckItem>
    {
        public void Report(CheckItem value) { lock (into) into.Add(value); }
    }
}
