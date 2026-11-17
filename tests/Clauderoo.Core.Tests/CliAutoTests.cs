using System.Net;
using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class CliAutoTests
{
    private const string WorkToken = "sk-ant-oat01-FAKE-work-0123456789";
    private const string HomeToken = "sk-ant-oat01-FAKE-home-0123456789";
    private static readonly DateTime Clock = new(2026, 11, 17, 12, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    // Answers by bearer token: the profile's 5-hour percent.
    private sealed class Handler(int homePercent, int workPercent) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var p = r.Headers.Authorization?.Parameter == HomeToken ? homePercent : workPercent;
            var body = $$"""{ "five_hour": { "utilization": {{p}}.0, "resets_at": "2026-11-17T15:40:00+00:00" } }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Clock);
        pm.Init();
        pm.Add("work");
        SignIn(pm.ConfigDirOf("personal"), HomeToken);
        SignIn(pm.ConfigDirOf("work"), WorkToken);
        pm.SetDefault("personal");
        return (pm, home);
    }

    private static void SignIn(string dir, string token)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            $$"""
            { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "refresh-FAKE",
              "expiresAt": {{Now.AddHours(2).ToUnixTimeMilliseconds()}}, "subscriptionType": "max" } }
            """);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, Handler h, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        using var client = new UsageClient(h, () => Now, "1.0");
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
            hookCancel: _ => new Noop(), usage: client, clock: () => Now);
        return (code, o.ToString(), e.ToString());
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }

    private static Handler Idle => new(10, 10);

    [Fact]
    public void Auto_On_SetsConfig()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, Idle, "auto", "on");
        Assert.Equal(0, r.code);
        Assert.True(pm.Config.AutoSwitch!.Enabled);
        Assert.Equal(90, pm.Config.AutoSwitch!.Threshold);

        Assert.Equal(0, Run(pm, Idle, "auto", "off").code);
        Assert.False(pm.Config.AutoSwitch!.Enabled);
    }

    [Fact]
    public void Auto_NoArgs_PrintsSettings()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on", "--threshold", "80", "--order", "work,personal");
        var r = Run(pm, Idle, "auto");
        Assert.Equal(0, r.code);
        Assert.Contains("on", r.o);
        Assert.Contains("80%", r.o);
        Assert.Contains("work, personal", r.o);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    public void Auto_Threshold_Validates1to100(string bad)
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, Idle, "auto", "--threshold", bad);
        Assert.Equal(1, r.code);
        Assert.Contains("6 to 100", r.e);
        Assert.Null(pm.Config.AutoSwitch);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("100")]
    public void Auto_Threshold_AcceptsBounds(string ok)
    {
        var (pm, home) = Make();
        using var _h = home;
        Assert.Equal(0, Run(pm, Idle, "auto", "--threshold", ok).code);
        Assert.Equal(int.Parse(ok), pm.Config.AutoSwitch!.Threshold);
    }

    [Fact]
    public void Auto_Order_UnknownProfile_Throws()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, Idle, "auto", "--order", "work,nope");
        Assert.Equal(1, r.code);
        Assert.Contains("Unknown profile", r.e);
        Assert.Null(pm.Config.AutoSwitch);
    }

    [Fact]
    public void AutoOnce_Switches_Exit0_DefaultChanged()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        var r = Run(pm, new Handler(95, 10), "auto", "--once");
        Assert.Equal(0, r.code);
        Assert.Contains("personal is at 95% of its 5-hour limit.", r.o);
        Assert.Contains("New terminals now use work.", r.o);
        Assert.Equal("work", pm.Config.DefaultProfile);
        var state = pm.AutoSwitchState.Load();
        Assert.Equal("personal", state.SwitchedFrom);
        Assert.Equal(Now, state.LastSwitch);
        Assert.DoesNotContain(HomeToken, r.o + r.e);
    }

    [Fact]
    public void AutoOnce_Nothing_Exit2()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        var r = Run(pm, new Handler(20, 10), "auto", "--once");
        Assert.Equal(2, r.code);
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void AutoOnce_Off_Exit2()
    {
        var (pm, home) = Make();
        using var _h = home;
        var r = Run(pm, new Handler(99, 10), "auto", "--once");
        Assert.Equal(2, r.code);
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void AutoOnce_NoTarget_Exit3()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        var r = Run(pm, new Handler(95, 95), "auto", "--once");
        Assert.Equal(3, r.code);
        Assert.Contains("personal is at 95%", r.o);
        Assert.Equal("personal", pm.Config.DefaultProfile);
        Assert.True(pm.AutoSwitchState.Load().NoTargetNotified);
        // already announced: now nothing to do
        Assert.Equal(2, Run(pm, new Handler(95, 95), "auto", "--once").code);
    }

    [Fact]
    public void AutoOnce_Json()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        var r = Run(pm, new Handler(95, 10), "auto", "--once", "--json");
        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.Equal("schemaVersion", root.EnumerateObject().First().Name);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("switched", root.GetProperty("action").GetString());
        Assert.Equal("personal", root.GetProperty("from").GetString());
        Assert.Equal("work", root.GetProperty("to").GetString());
        Assert.Contains("95%", root.GetProperty("reason").GetString());

        var none = Run(pm, new Handler(10, 10), "auto", "--once", "--json");
        Assert.Equal(2, none.code);
        using var d2 = JsonDocument.Parse(none.o);
        Assert.Equal("none", d2.RootElement.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, d2.RootElement.GetProperty("to").ValueKind);
    }

    [Fact]
    public void Default_Cli_NotesManualHold()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        Assert.Equal(0, Run(pm, Idle, "default", "work").code);
        Assert.Equal("work", pm.AutoSwitchState.Load().ManualHold);
        Assert.Equal(0, Run(pm, Idle, "default", "--clear").code);
        Assert.Equal("personal", pm.AutoSwitchState.Load().ManualHold);

        // The held default is over the threshold: --once leaves it alone.
        var r = Run(pm, new Handler(95, 10), "auto", "--once");
        Assert.Equal(2, r.code);
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void AutoOnce_DoesNotNoteManualHold()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        Assert.Equal(0, Run(pm, new Handler(95, 10), "auto", "--once").code);
        Assert.Equal("work", pm.Config.DefaultProfile);
        Assert.Null(pm.AutoSwitchState.Load().ManualHold);
    }

    [Fact]
    public void AutoOnce_Json_NoTarget()
    {
        var (pm, home) = Make();
        using var _h = home;
        Run(pm, Idle, "auto", "on");
        var r = Run(pm, new Handler(95, 95), "auto", "--once", "--json");
        Assert.Equal(3, r.code);
        using var doc = JsonDocument.Parse(r.o);
        Assert.Equal("noTarget", doc.RootElement.GetProperty("action").GetString());
    }
}
