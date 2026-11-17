namespace Clauderoo.Core.Tests;

public class AutoSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly AutoSwitchState Fresh = new(null, null, false, false);

    private static ClauderooConfig Cfg(string? def, bool enabled = true, string[]? order = null, params (string name, string? provider)[] profiles)
    {
        var cfg = new ClauderooConfig { DefaultProfile = def };
        foreach (var (name, provider) in profiles) cfg.Profiles[name] = new ProfileEntry { Provider = provider };
        cfg.AutoSwitch = new AutoSwitchSettings { Enabled = enabled, Order = order?.ToList() ?? [] };
        return cfg;
    }

    private static UsageReading R(string p, double five, double seven, int ageMin = 1) =>
        new(p, Now.AddMinutes(-ageMin), "max", [new UsageWindow("5h", five, null), new UsageWindow("7d", seven, null)]);

    private static Dictionary<string, UsageReading> U(params UsageReading[] rs) => rs.ToDictionary(r => r.Profile);

    private static ClauderooConfig TwoLogin(string def = "personal", bool enabled = true) =>
        Cfg(def, enabled, null, ("personal", null), ("work", null));

    [Fact]
    public void Decide_Disabled_NoAction() =>
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(enabled: false), U(R("personal", 95, 10), R("work", 1, 1)), Now, Fresh));

    [Fact]
    public void Decide_NoSettings_NoAction()
    {
        var cfg = TwoLogin(); cfg.AutoSwitch = null;
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("work", 1, 1)), Now, Fresh));
    }

    [Fact]
    public void Decide_BelowThreshold_NoAction() =>
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 89, 89), R("work", 1, 1)), Now, Fresh));

    [Fact]
    public void Decide_StaleReading_NoAction() =>
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 95, 10, ageMin: 16), R("work", 1, 1)), Now, Fresh));

    [Fact]
    public void Decide_Over_PicksFirstInOrderWithRoom()
    {
        var cfg = Cfg("personal", true, ["busy", "work", "other"],
            ("personal", null), ("work", null), ("other", null), ("busy", null));
        var d = AutoSwitch.Decide(cfg, U(R("personal", 92, 10), R("busy", 88, 1), R("work", 10, 10), R("other", 1, 1)), Now, Fresh);
        var s = Assert.IsType<Decision.SwitchTo>(d);
        Assert.Equal("work", s.Profile);
        Assert.Equal("personal is at 92% of its 5-hour limit.", s.Reason);
    }

    [Fact]
    public void Decide_SevenDayWindow_ReasonSaysSevenDay()
    {
        var d = AutoSwitch.Decide(TwoLogin(), U(R("personal", 10, 93), R("work", 1, 1)), Now, Fresh);
        Assert.Equal("personal is at 93% of its 7-day limit.", Assert.IsType<Decision.SwitchTo>(d).Reason);
    }

    [Fact]
    public void Decide_PerModelWindow_DoesNotTrigger()
    {
        var reading = new UsageReading("personal", Now, "max", [new UsageWindow("5h", 10, null), new UsageWindow("7d", 10, null), new UsageWindow("7d-opus", 99, null)]);
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(), U(reading, R("work", 1, 1)), Now, Fresh));
    }

    [Fact]
    public void Decide_TargetWithinHysteresis_NotChosen()
    {
        var d = AutoSwitch.Decide(TwoLogin(), U(R("personal", 92, 10), R("work", 86, 10)), Now, Fresh);
        Assert.IsType<Decision.NoTarget>(d);
    }

    [Fact]
    public void Decide_TargetJustBelowHysteresis_Chosen()
    {
        var d = AutoSwitch.Decide(TwoLogin(), U(R("personal", 92, 10), R("work", 84.9, 84.9)), Now, Fresh);
        Assert.IsType<Decision.SwitchTo>(d);
    }

    [Fact]
    public void Decide_TargetStaleReading_Skipped() =>
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 92, 10), R("work", 1, 1, ageMin: 20)), Now, Fresh));

    [Fact]
    public void Decide_TargetNoReading_Skipped() =>
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 92, 10)), Now, Fresh));

    [Fact]
    public void Decide_WithinCooldown_NoAction()
    {
        var state = new AutoSwitchState(Now.AddMinutes(-9), "other", false, false);
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 95, 10), R("work", 1, 1)), Now, state));
        var after = new AutoSwitchState(Now.AddMinutes(-10), "other", false, false);
        Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 95, 10), R("work", 1, 1)), Now, after));
    }

    [Fact]
    public void Decide_TokenProfileNeverTarget()
    {
        // Even when listed explicitly, a token profile is not a target.
        var cfg = Cfg("personal", true, ["tok"], ("personal", null), ("tok", "token"));
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("tok", 1, 1)), Now, Fresh));
        var unlisted = Cfg("personal", true, null, ("personal", null), ("tok", "token"));
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(unlisted, U(R("personal", 95, 10)), Now, Fresh));
    }

    [Fact]
    public void Decide_VertexOnlyIfListed()
    {
        var listed = Cfg("personal", true, ["vtx"], ("personal", null), ("vtx", Providers.Vertex), ("key", Providers.ApiKey));
        var s = Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(listed, U(R("personal", 95, 10)), Now, Fresh));
        Assert.Equal("vtx", s.Profile);

        var key = Cfg("personal", true, ["key"], ("personal", null), ("vtx", Providers.Vertex), ("key", Providers.ApiKey));
        Assert.Equal("key", Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(key, U(R("personal", 95, 10)), Now, Fresh)).Profile);

        // Empty order means list order, which is not an explicit listing: vertex and api-key are skipped.
        var empty = Cfg("personal", true, null, ("personal", null), ("vtx", Providers.Vertex), ("key", Providers.ApiKey));
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(empty, U(R("personal", 95, 10)), Now, Fresh));
    }

    [Fact]
    public void Decide_NoCandidates_NoTarget()
    {
        var cfg = Cfg("personal", true, null, ("personal", null));
        var t = Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10)), Now, Fresh));
        Assert.Equal("personal is at 95% of its 5-hour limit.", t.Reason);
    }

    [Fact]
    public void Decide_NoTarget_OnlyOncePerCrossing()
    {
        var cfg = Cfg("personal", true, null, ("personal", null));
        var state = new AutoSwitchState(null, null, true, false);
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10)), Now, state));
    }

    [Fact]
    public void Decide_OrderNamesUnknownProfile_Skipped()
    {
        var cfg = Cfg("personal", true, ["ghost", "work"], ("personal", null), ("work", null));
        Assert.Equal("work", Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("work", 1, 1)), Now, Fresh)).Profile);
    }

    [Fact]
    public void Decide_EmptyOrder_UsesListOrder()
    {
        var cfg = Cfg("personal", true, null, ("personal", null), ("a", null), ("b", null));
        var d = AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("a", 1, 1), R("b", 1, 1)), Now, Fresh);
        Assert.Equal("a", Assert.IsType<Decision.SwitchTo>(d).Profile);
    }

    [Fact]
    public void Decide_OriginalRecovered_RecoveredOnce()
    {
        // Switched from personal to work; personal is back under the margin and work is the default now.
        var cfg = TwoLogin(def: "work");
        var usage = U(R("work", 20, 20), R("personal", 10, 10));
        var state = new AutoSwitchState(Now.AddMinutes(-30), "personal", false, false);
        var r = Assert.IsType<Decision.Recovered>(AutoSwitch.Decide(cfg, usage, Now, state));
        Assert.Equal("personal", r.Profile);

        var notified = state with { RecoveredNotified = true };
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(cfg, usage, Now, notified));
    }

    [Fact]
    public void Decide_OriginalStillHigh_NotRecovered()
    {
        var cfg = TwoLogin(def: "work");
        var state = new AutoSwitchState(Now.AddMinutes(-30), "personal", false, false);
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(cfg, U(R("work", 20, 20), R("personal", 88, 10)), Now, state));
    }

    [Fact]
    public void Decide_RecoveredButOriginalIsDefaultAgain_NoAction()
    {
        var state = new AutoSwitchState(Now.AddMinutes(-30), "personal", false, false);
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 10, 10)), Now, state));
    }

    [Fact]
    public void Store_RoundTripsAndMissingFileIsFresh()
    {
        using var h = new TempHome();
        var store = new AutoSwitchStore(h.Paths);
        Assert.Equal(new AutoSwitchState(null, null, false, false), store.Load());
        var s = new AutoSwitchState(Now, "personal", true, true);
        store.Save(s);
        Assert.Equal(s, store.Load());
        Assert.True(File.Exists(Path.Combine(h.Paths.Root, "autoswitch.json")));
    }

    [Fact]
    public void Store_CorruptFile_TreatedAsFresh()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(Path.Combine(h.Paths.Root, "autoswitch.json"), "{not json");
        Assert.Equal(new AutoSwitchState(null, null, false, false), new AutoSwitchStore(h.Paths).Load());
    }

    [Fact]
    public void Config_AutoSwitch_RoundTripsCamelCase()
    {
        using var h = new TempHome();
        var cfg = new ClauderooConfig { AutoSwitch = new AutoSwitchSettings { Enabled = true, Threshold = 80, Order = ["a", "b"] } };
        ConfigStore.Save(h.Paths, cfg);
        var text = File.ReadAllText(h.Paths.ConfigFile);
        Assert.Contains("\"autoSwitch\"", text);
        var back = ConfigStore.Load(h.Paths)!.AutoSwitch!;
        Assert.True(back.Enabled);
        Assert.Equal(80, back.Threshold);
        Assert.Equal(["a", "b"], back.Order);
    }

    private static (ProfileManager pm, TempHome h) MakePm()
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => new DateTime(2026, 11, 17, 10, 0, 0), new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        pm.Add("spare");
        var cfg = pm.Config;
        cfg.AutoSwitch = new AutoSwitchSettings { Enabled = true, Order = ["work", "spare"] };
        ConfigStore.Save(h.Paths, cfg);
        return (pm, h);
    }

    [Fact]
    public void Rename_UpdatesOrder()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.Rename("work", "job");
        Assert.Equal(["job", "spare"], pm.Config.AutoSwitch!.Order);
    }

    [Fact]
    public void Remove_DropsFromOrder()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.Remove("work");
        Assert.Equal(["spare"], pm.Config.AutoSwitch!.Order);
    }
}
