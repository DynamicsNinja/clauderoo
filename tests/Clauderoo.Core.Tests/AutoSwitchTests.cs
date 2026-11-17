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
        var cfg = Cfg("personal", true, ["tok"], ("personal", null), ("tok", Providers.Token));
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("tok", 1, 1)), Now, Fresh));
        var unlisted = Cfg("personal", true, null, ("personal", null), ("tok", Providers.Token));
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
        var state = new AutoSwitchState(null, null, true, false, "personal");
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

        var notified = state with { RecoveredNotified = true, FlagsFor = "work" };
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

    [Fact]
    public void Decide_NullOrder_TreatedAsEmpty()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(h.Paths.ConfigFile,
            "{\"defaultProfile\":\"personal\",\"profiles\":{\"personal\":{},\"work\":{}},\"autoSwitch\":{\"enabled\":true,\"order\":null}}");
        var cfg = ConfigStore.Load(h.Paths)!;
        Assert.Empty(cfg.AutoSwitch!.Order);
        var d = AutoSwitch.Decide(cfg, U(R("personal", 95, 10), R("work", 1, 1)), Now, Fresh);
        Assert.Equal("work", Assert.IsType<Decision.SwitchTo>(d).Profile);
    }

    [Fact]
    public void Remove_NullOrder_DoesNotThrow()
    {
        var (pm, h) = MakePm(); using var _h = h;
        File.WriteAllText(h.Paths.ConfigFile, System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(h.Paths.ConfigFile), "\"order\":\\s*\\[[^\\]]*\\]", "\"order\": null"));
        Assert.Contains("\"order\": null", File.ReadAllText(h.Paths.ConfigFile));
        pm.Remove("work");
        Assert.Empty(pm.Config.AutoSwitch!.Order);
    }

    // ---- Next: flag transitions ----

    private static AutoSwitchState Next(AutoSwitchState s, Decision d, string? def, UsageReading[] usage, int threshold = 90) =>
        AutoSwitch.Next(s, d, def, U(usage), Now, new AutoSwitchSettings { Enabled = true, Threshold = threshold });

    [Fact]
    public void Next_SwitchTo_RecordsSwitchAndClearsFlags()
    {
        var s = new AutoSwitchState(null, null, true, true, "personal");
        var n = Next(s, new Decision.SwitchTo("work", "x"), "personal", []);
        Assert.Equal(new AutoSwitchState(Now, "personal", false, false, "work"), n);
    }

    [Fact]
    public void Next_NoTarget_SetsFlagScopedToDefault()
    {
        var n = Next(Fresh, new Decision.NoTarget("x"), "personal", []);
        Assert.True(n.NoTargetNotified);
        Assert.Equal("personal", n.FlagsFor);
    }

    [Fact]
    public void Next_Recovered_SetsFlag()
    {
        var s = new AutoSwitchState(Now.AddMinutes(-30), "personal", false, false, "work");
        var n = Next(s, new Decision.Recovered("personal"), "work", []);
        Assert.True(n.RecoveredNotified);
        Assert.Equal("personal", n.SwitchedFrom);
    }

    [Fact]
    public void Next_NoAction_DefaultBackUnderThreshold_ClearsNoTarget()
    {
        var s = new AutoSwitchState(null, null, true, false, "personal");
        var n = Next(s, new Decision.NoAction(), "personal", [R("personal", 50, 50)]);
        Assert.False(n.NoTargetNotified);
    }

    [Fact]
    public void Next_NoAction_StillOverOrStale_KeepsNoTarget()
    {
        var s = new AutoSwitchState(null, null, true, false, "personal");
        Assert.True(Next(s, new Decision.NoAction(), "personal", [R("personal", 95, 50)]).NoTargetNotified);
        Assert.True(Next(s, new Decision.NoAction(), "personal", [R("personal", 10, 10, ageMin: 30)]).NoTargetNotified);
    }

    [Fact]
    public void Next_DefaultChanged_ClearsBothFlags()
    {
        var s = new AutoSwitchState(null, "x", true, true, "personal");
        var n = Next(s, new Decision.NoAction(), "work", [R("work", 99, 99)]);
        Assert.False(n.NoTargetNotified);
        Assert.False(n.RecoveredNotified);
        Assert.Equal("work", n.FlagsFor);
        Assert.Equal("x", n.SwitchedFrom);
    }

    [Fact]
    public void Decide_ManualHold_NoSwitchUntilBackUnder()
    {
        var cfg = TwoLogin();
        var usage = U(R("personal", 95, 10), R("work", 1, 1));
        var held = Fresh with { ManualHold = "personal" };
        // The user chose personal while it was over: no switch, no NoTarget, for this crossing.
        Assert.IsType<Decision.NoAction>(AutoSwitch.Decide(cfg, usage, Now, held));

        // Back under the threshold: Next drops the hold, so the next crossing switches again.
        var n = AutoSwitch.Next(held, new Decision.NoAction(), "personal", U(R("personal", 50, 10), R("work", 1, 1)), Now, cfg.AutoSwitch);
        Assert.Null(n.ManualHold);
        Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(cfg, usage, Now, n));

        // A hold for another profile doesn't stop the default from switching.
        Assert.IsType<Decision.SwitchTo>(AutoSwitch.Decide(cfg, usage, Now, Fresh with { ManualHold = "work" }));
    }

    [Fact]
    public void Next_ManualHold_ClearedWhenUnderOrChanged()
    {
        var held = Fresh with { ManualHold = "personal" };
        // Still over, or no fresh reading: kept.
        Assert.Equal("personal", Next(held, new Decision.NoAction(), "personal", [R("personal", 95, 10)]).ManualHold);
        Assert.Equal("personal", Next(held, new Decision.NoAction(), "personal", [R("personal", 10, 10, ageMin: 30)]).ManualHold);
        Assert.Equal("personal", Next(held, new Decision.NoAction(), "personal", []).ManualHold);
        // Fresh reading back under: cleared.
        Assert.Null(Next(held, new Decision.NoAction(), "personal", [R("personal", 89, 10)]).ManualHold);
        // The default changed: cleared.
        Assert.Null(Next(held, new Decision.NoAction(), "work", [R("work", 95, 10)]).ManualHold);
        // An automatic switch: cleared.
        Assert.Null(Next(held, new Decision.SwitchTo("work", "r"), "personal", [R("personal", 95, 10)]).ManualHold);
    }

    [Fact]
    public void Store_NoteManualDefault_SetsHoldAndKeepsTheRest()
    {
        using var h = new TempHome();
        var store = new AutoSwitchStore(h.Paths);
        store.Save(new AutoSwitchState(Now, "work", true, false, "personal"));
        store.NoteManualDefault("personal");
        Assert.Equal(new AutoSwitchState(Now, "work", true, false, "personal", "personal"), store.Load());

        // An old file without the field still loads.
        File.WriteAllText(Path.Combine(h.Paths.Root, "autoswitch.json"),
            """{ "lastSwitch": null, "switchedFrom": "work", "noTargetNotified": false, "recoveredNotified": false }""");
        Assert.Null(store.Load().ManualHold);
        Assert.Equal("work", store.Load().SwitchedFrom);

        // Rename and remove carry the hold along.
        store.NoteManualDefault("work");
        store.Rename("work", "job");
        Assert.Equal("job", store.Load().ManualHold);
        store.Remove("job");
        Assert.Null(store.Load().ManualHold);
    }

    [Fact]
    public void Decide_StaleFlagFromOtherDefault_DoesNotSuppress()
    {
        var cfg = Cfg("personal", true, null, ("personal", null));
        var stale = new AutoSwitchState(null, null, true, false, "work");
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(cfg, U(R("personal", 95, 10)), Now, stale));
    }

    [Fact]
    public void Lifecycle_NoTargetNotifiesAgainOnNextCrossing()
    {
        var cfg = Cfg("personal", true, null, ("personal", null));
        var s = Fresh;
        var over = U(R("personal", 95, 10));
        var d1 = AutoSwitch.Decide(cfg, over, Now, s);
        Assert.IsType<Decision.NoTarget>(d1);
        s = AutoSwitch.Next(s, d1, "personal", over, Now, cfg.AutoSwitch);
        var d2 = AutoSwitch.Decide(cfg, over, Now, s);
        Assert.IsType<Decision.NoAction>(d2);
        s = AutoSwitch.Next(s, d2, "personal", over, Now, cfg.AutoSwitch);
        var under = U(R("personal", 40, 10));
        s = AutoSwitch.Next(s, AutoSwitch.Decide(cfg, under, Now, s), "personal", under, Now, cfg.AutoSwitch);
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(cfg, over, Now, s));
    }

    // ---- state follows rename and remove ----

    [Fact]
    public void Store_Rename_RenamesSwitchedFromAndFlagsFor()
    {
        using var h = new TempHome();
        var store = new AutoSwitchStore(h.Paths);
        store.Save(new AutoSwitchState(Now, "work", true, false, "work"));
        store.Rename("work", "job");
        Assert.Equal(new AutoSwitchState(Now, "job", true, false, "job"), store.Load());
    }

    [Fact]
    public void Store_Remove_ClearsSwitchedFromAndFlags()
    {
        using var h = new TempHome();
        var store = new AutoSwitchStore(h.Paths);
        store.Save(new AutoSwitchState(Now, "work", true, true, "work"));
        store.Remove("work");
        Assert.Equal(new AutoSwitchState(Now, null, false, false, null), store.Load());
    }

    [Fact]
    public void Store_RenameOrRemoveUnrelated_NoFileWritten()
    {
        using var h = new TempHome();
        var store = new AutoSwitchStore(h.Paths);
        store.Rename("a", "b");
        store.Remove("a");
        Assert.False(File.Exists(Path.Combine(h.Paths.Root, "autoswitch.json")));
    }

    [Fact]
    public void Rename_UpdatesSwitchedFromInState()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.AutoSwitchState.Save(new AutoSwitchState(Now, "work", false, false));
        pm.Rename("work", "job");
        Assert.Equal("job", pm.AutoSwitchState.Load().SwitchedFrom);
    }

    [Fact]
    public void Remove_ClearsSwitchedFromInState()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.AutoSwitchState.Save(new AutoSwitchState(Now, "work", false, false));
        pm.Remove("work");
        Assert.Null(pm.AutoSwitchState.Load().SwitchedFrom);
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

    // ---- Exact boundaries ----

    [Fact]
    public void Decide_FiveHourExactlyAtThreshold_Triggers() =>
        Assert.Equal("work", Assert.IsType<Decision.SwitchTo>(
            AutoSwitch.Decide(TwoLogin(), U(R("personal", 90, 10), R("work", 1, 1)), Now, Fresh)).Profile);

    [Fact]
    public void Decide_TargetExactlyAtMargin_Refused() =>
        // threshold 90 - hysteresis 5 = 85: a target must be strictly under it
        Assert.IsType<Decision.NoTarget>(AutoSwitch.Decide(TwoLogin(), U(R("personal", 95, 10), R("work", 85, 1)), Now, Fresh));

    [Fact]
    public void Decide_ReadingExactlyFifteenMinutesOld_IsFresh() =>
        Assert.Equal("work", Assert.IsType<Decision.SwitchTo>(
            AutoSwitch.Decide(TwoLogin(), U(R("personal", 95, 10, ageMin: 15), R("work", 1, 1, ageMin: 15)), Now, Fresh)).Profile);

    // ---- RunPass: the one shared pass ----

    private static Dictionary<string, UsageReading> OverOnPersonal() =>
        U(R("personal", 95, 10), R("work", 1, 1), R("spare", 1, 1));

    [Fact]
    public void RunPass_Switches_ThroughSetDefault_NoHold_SavesState()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.SetDefault("personal");
        var r = AutoSwitchRunner.RunPass(pm, OverOnPersonal(), Now);
        Assert.Equal("work", Assert.IsType<Decision.SwitchTo>(r.Decision).Profile);
        Assert.Equal("personal", r.From);
        Assert.NotNull(r.Report);
        Assert.Null(r.ReadError);
        Assert.Equal("work", pm.Config.DefaultProfile);
        var s = pm.AutoSwitchState.Load();
        Assert.Equal("personal", s.SwitchedFrom);
        Assert.Equal(Now, s.LastSwitch);
        Assert.Null(s.ManualHold);
    }

    [Fact]
    public void RunPass_DefaultChangedBeforeApply_NotOverridden()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.SetDefault("personal");
        // Between the decision and the switch, the user picks spare by hand, even though it is over the threshold too
        // (so the hold stays: it only clears once the held profile is back under).
        var usage = U(R("personal", 95, 10), R("work", 1, 1), R("spare", 95, 10));
        var r = AutoSwitchRunner.RunPass(pm, usage, Now, beforeApply: () => pm.SetDefaultByUser("spare"));
        Assert.IsType<Decision.NoAction>(r.Decision);
        Assert.Equal("spare", r.From); // Next ran with the actual default
        Assert.Null(r.Report);
        Assert.Equal("spare", pm.Config.DefaultProfile);
        var s = pm.AutoSwitchState.Load();
        Assert.Equal("spare", s.ManualHold); // the hold the user's choice noted survives the save
        Assert.Null(s.LastSwitch);
        Assert.Null(s.SwitchedFrom);
    }

    [Fact]
    public void RunPass_ReadsConfigFresh()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.SetDefault("personal");
        var cfg = pm.Config;
        cfg.AutoSwitch!.Enabled = false;
        ConfigStore.Save(h.Paths, cfg);
        var r = AutoSwitchRunner.RunPass(pm, OverOnPersonal(), Now);
        Assert.IsType<Decision.NoAction>(r.Decision);
        Assert.Equal("personal", pm.Config.DefaultProfile);
    }

    [Fact]
    public void RunPass_UnreadableConfig_ReportsReadError_DoesNothing()
    {
        var (pm, h) = MakePm(); using var _h = h;
        pm.SetDefault("personal");
        File.WriteAllText(h.Paths.ConfigFile, "{not json");
        var r = AutoSwitchRunner.RunPass(pm, OverOnPersonal(), Now);
        Assert.NotNull(r.ReadError);
        Assert.IsType<Decision.NoAction>(r.Decision);
        Assert.False(File.Exists(Path.Combine(h.Paths.Root, "autoswitch.json")));
    }

    [Fact]
    public void Settings_UnknownFields_SurviveSave()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(h.Paths.ConfigFile,
            "{\"profiles\":{},\"autoSwitch\":{\"enabled\":true,\"threshold\":80,\"order\":[],\"quietHours\":{\"from\":22}}}");
        var cfg = ConfigStore.Load(h.Paths)!;
        cfg.AutoSwitch!.Threshold = 75;
        ConfigStore.Save(h.Paths, cfg);
        var text = File.ReadAllText(h.Paths.ConfigFile);
        Assert.Contains("quietHours", text);
        Assert.Equal(75, ConfigStore.Load(h.Paths)!.AutoSwitch!.Threshold);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"threshold\""));
    }
}
