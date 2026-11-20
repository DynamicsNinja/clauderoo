using Clauderoo.Core;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class TrayTextTests
{
    static ProfileInfo Login(string name, string? email, bool home = false, bool isDefault = false) =>
        new(name, home, isDefault, email, [], @"C:\x\" + name, Providers.Login);

    static ProfileInfo Vertex(string name, string? project) =>
        new(name, false, false, null, [], @"C:\x\" + name, Providers.Vertex, project);

    static ProfileInfo ApiKey(string name) => new(name, false, false, null, [], @"C:\x\" + name, Providers.ApiKey);

    static Problem P(ProblemFix? fix, string profile = "work", string title = "t") => new(profile, title, profile + ": raw", fix);

    [Fact]
    public void ImportSummary_Text()
    {
        var r = new ImportResult(["a"], ["b", "c"], ["d: already exists (use --force to update it)"], ["e: needs an API key"], ["f"],
            Notices: ["a: start folder X doesn't exist on this machine, so it wasn't set."], Mapped: ["C:\\w → b"], EnvNames: ["a: A_VAR"]);
        var nl = Environment.NewLine;
        Assert.Equal(string.Join(nl, "Created:", "  a", "Updated:", "  b", "  c", "Folder mappings added:", "  C:\\w → b",
            "Environment variables (names only):", "  a: A_VAR", "Skipped:",
            "  d: already exists (answer Yes to update existing profiles)", "Kept their sign-in:", "  f",
            "Needs a key or token:", "  e: needs an API key",
            "Notices:", "  a: start folder X doesn't exist on this machine, so it wasn't set."), TrayText.ImportSummary(r));
        Assert.Equal("Nothing was imported.", TrayText.ImportSummary(new ImportResult([], [], [], [])));
        // A mapping alone is a change.
        Assert.DoesNotContain("Nothing was imported.", TrayText.ImportSummary(new ImportResult([], [], [], [], Mapped: ["C:\\w → b"])));
        Assert.StartsWith("Nothing was imported.", TrayText.ImportSummary(new ImportResult([], [], ["x: already exists"], [])));
    }

    [Fact]
    public void ImportSummary_AutoSwitch_SaysItIsOff() =>
        Assert.Equal("Auto-switch settings imported (off — turn on with 'cr auto on' or the tray)",
            TrayText.ImportSummary(new ImportResult([], [], [], [], AutoSwitchImported: true)));

    [Fact]
    public void Label_Login_WithEmail()
    {
        Assert.Equal("personal · someone@example.com",
            TrayText.ProfileLabel(Login("personal", "someone@example.com", home: true)));
    }

    [Fact]
    public void Label_Login_NotSignedIn()
    {
        var p = Login("plain", null);
        Assert.Equal("plain · not signed in", TrayText.ProfileLabel(p));
        Assert.True(TrayText.IsSignedOut(p));
    }

    [Fact]
    public void Label_Vertex_And_ApiKey()
    {
        Assert.Equal("work · acme-prod", TrayText.ProfileLabel(Vertex("work", "acme-prod")));
        Assert.Equal("work · Google Vertex AI", TrayText.ProfileLabel(Vertex("work", null)));
        Assert.Equal("api · API key", TrayText.ProfileLabel(ApiKey("api")));
        Assert.False(TrayText.IsSignedOut(ApiKey("api")));
    }

    [Fact]
    public void ProviderLabel_Token_IsClaudeToken()
    {
        var p = new ProfileInfo("ci", false, false, null, [], @"C:\x\ci", Providers.Token);
        Assert.Equal("Claude token", TrayText.ProviderName(Providers.Token));
        Assert.Equal("ci · Claude token", TrayText.ProfileLabel(p));
        Assert.False(TrayText.IsSignedOut(p)); // no email to show, and that's not "not signed in"
        Assert.Null(TrayText.Account(p));
    }

    [Fact]
    public void MenuItems_IncludeStartIsolated()
    {
        string? started = null;
        using var menu = new ContextMenuStrip();
        var state = new TrayMenuState([Login("work", "w@example.com", isDefault: true)], [],
            new Dictionary<string, int>(), false);
        TrayMenu.Build(menu, state, new TrayMenuActions { StartIsolated = n => started = n });

        var profile = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text!.StartsWith("work"));
        var sub = profile.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        var iso = Assert.Single(sub, i => i.Text == "Start &isolated session");
        Assert.Equal(1, sub.FindIndex(i => i == iso)); // right after "Start session"
        Assert.False(string.IsNullOrEmpty(iso.ToolTipText));
        iso.PerformClick();
        Assert.Equal("work", started);
    }

    [Fact]
    public void Account_CutAt40()
    {
        var email = new string('a', 60) + "@example.com";
        var account = TrayText.Account(Login("x", email))!;
        Assert.Equal(TrayText.AccountMax, account.Length);
        Assert.EndsWith("…", account);
        Assert.Equal("short@example.com", TrayText.Account(Login("x", "short@example.com")));
        Assert.Equal(40, TrayText.Account(Vertex("w", new string('p', 41)))!.Length);
        Assert.Equal(new string('p', 40), TrayText.Account(Vertex("w", new string('p', 40))));
    }

    [Fact]
    public void Tooltip_ThreeLines()
    {
        var t = TrayText.Tooltip(Vertex("work", "acme-prod"), 2, 1);
        Assert.Equal("clauderoo — double-click starts work\nGoogle Vertex AI · acme-prod\n⚠ 2 problems · ● 1 session running", t);
        Assert.Equal("clauderoo — double-click starts work\nGoogle Vertex AI · acme-prod\nNo problems",
            TrayText.Tooltip(Vertex("work", "acme-prod"), 0, 0));
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(60, 12, 3)]
    [InlineData(120, 1, 1)]
    [InlineData(300, 999, 999)]
    public void Tooltip_NeverOver127(int nameLength, int problems, int running)
    {
        var name = new string('n', nameLength);
        foreach (var p in new[] { Login(name, new string('e', 200) + "@example.com"), Vertex(name, new string('p', 200)), ApiKey(name) })
        {
            var t = TrayText.Tooltip(p, problems, running);
            Assert.True(t.Length <= TrayText.TooltipMax, $"{t.Length}: {t}");
            Assert.Equal(2, t.Count(c => c == '\n'));
        }
        Assert.True(TrayText.Tooltip(null, problems, running).Length <= TrayText.TooltipMax);
    }

    [Fact]
    public void Tooltip_LongEmail_KeepsStatusLine()
    {
        var t = TrayText.Tooltip(Login("a-rather-long-profile-name-for-work", new string('e', 80) + "@example.com"), 4, 2);
        Assert.EndsWith("⚠ 4 problems · ● 2 sessions running", t);
        Assert.StartsWith("clauderoo — double-click starts a-rather-long-profile-name-for-work\n", t);
    }

    [Fact]
    public void IconState_Precedence()
    {
        Assert.Equal(AppIconState.Normal, TrayText.IconState([], 0));
        Assert.Equal(AppIconState.Running, TrayText.IconState([], 2));
        Assert.Equal(AppIconState.Warning, TrayText.IconState([P(ProblemFix.Resync)], 2));
        Assert.Equal(AppIconState.Error, TrayText.IconState([P(ProblemFix.Resync), P(null)], 2));
        Assert.Equal(AppIconState.Error, TrayText.IconState([P(null)], 0));
    }

    [Fact]
    public void Key_IsProfileAndTitle()
    {
        Assert.Equal(TrayText.Key(new Problem("w", "T", "raw 1", null)), TrayText.Key(new Problem("w", "T", "raw 2", ProblemFix.Resync)));
        Assert.NotEqual(TrayText.Key(new Problem("w", "T", "r", null)), TrayText.Key(new Problem("v", "T", "r", null)));
    }

    [Fact]
    public void Explain_ShareAgain_NamesProfileAndFolder()
    {
        var p = ProblemTranslator.Translate("work: skills is a real folder, not shared (run 'clauderoo share work skills' to share it)");
        Assert.Equal("Share it again replaces work's own skills folder with the shared one; clauderoo keeps a backup of the old folder.",
            TrayText.Explain(p));
    }

    [Fact]
    public void Explain_NeverShowsCliText()
    {
        foreach (var raw in new[]
                 {
                     "work: skills is a real folder, not shared (run 'clauderoo share work skills' to share it)",
                     "api: settings.json is not valid JSON: bad",
                     "x: could not sync mcpServers: locked",
                     "nonsense",
                 })
        {
            var text = TrayText.Explain(ProblemTranslator.Translate(raw));
            Assert.DoesNotContain("clauderoo share", text);
            Assert.DoesNotContain("run '", text);
        }
    }

    [Fact]
    public void UpdateTexts_NameTheVersion()
    {
        Assert.Equal("&Update to v1.2.0", TrayText.UpdateItem("1.2.0"));
        Assert.Contains("v1.2.0", TrayText.UpdateBalloon("1.2.0"));
    }

    [Fact]
    public void Header_AllGoodOrProblems()
    {
        Assert.Equal("clauderoo — all good", TrayText.Header(0));
        Assert.Equal("1 &problem…", TrayText.Header(1));
        Assert.Equal("3 &problems…", TrayText.Header(3));
    }

    [Fact]
    public void ConfigProblem_Wording()
    {
        Assert.Equal("clauderoo isn't set up yet", TrayText.ConfigProblem(ConfigIssue.Missing).Headline);
        Assert.Equal("clauderoo's settings file is damaged", TrayText.ConfigProblem(ConfigIssue.Damaged, "bad json").Headline);
        Assert.Equal("bad json", TrayText.ConfigProblem(ConfigIssue.Damaged, "bad json").Detail);
        Assert.Equal("clauderoo's settings can't be read right now", TrayText.ConfigProblem(ConfigIssue.Unreadable).Headline);
    }

    [Fact]
    public void SetUpFailed_PlainWords()
    {
        Assert.StartsWith("Claude is already set to use another folder",
            TrayText.SetUpFailed("CLAUDE_CONFIG_DIR is set to 'x'. Clear it, or run 'clauderoo init --home <path>'."));
        Assert.StartsWith("Claude hasn't been used on this PC yet", TrayText.SetUpFailed(@"Home folder not found: C:\u\.claude"));
    }

    // ---- final review fixes ----

    [Fact]
    public void AccessKey_RadioLabels_UseTrayTerms()
    {
        Assert.Equal("Claude &login", TrayText.AccessKey(TrayText.ClaudeLogin, "login"));
        Assert.Equal("Google Verte&x AI", TrayText.AccessKey(TrayText.Vertex, "x AI"));
        Assert.Equal("&API key", TrayText.AccessKey(TrayText.ApiKey, "API"));
        Assert.Equal("abc", TrayText.AccessKey("abc", "z"));
    }

    [Fact]
    public void PlainError_LauncherRefusal_NoCliHint()
    {
        var (text, unknown) = TrayText.PlainError(new ClauderooException(
            "Path 'C:\\a%b' contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead."));
        Assert.False(unknown);
        Assert.DoesNotContain("clauderoo run", text);
        Assert.StartsWith("This session can't be opened in a new tab", text);
    }

    [Fact]
    public void PlainError_UnknownProfile_Plain() =>
        Assert.Equal(("That profile doesn't exist any more.", false),
            TrayText.PlainError(new ClauderooException("Unknown profile 'x'.")));

    [Fact]
    public void PlainError_PlainCoreMessage_Kept() =>
        Assert.Equal(("clauderoo isn't installed properly — reinstall it.", false),
            TrayText.PlainError(new ClauderooException("clauderoo isn't installed properly — reinstall it.")));

    [Theory]
    [InlineData("Refused: run 'clauderoo remove x --force'.")]
    [InlineData("x is busy; use 'cr start x' instead.")]
    public void PlainError_CliWording_IsUnknown(string message) =>
        Assert.Equal((TrayText.SomethingWentWrong, true), TrayText.PlainError(new ClauderooException(message)));

    [Fact]
    public void PlainError_OtherException_IsUnknown() =>
        Assert.Equal((TrayText.SomethingWentWrong, true),
            TrayText.PlainError(new System.ComponentModel.Win32Exception("The system cannot find the file specified")));

    [Fact]
    public void ProfilesForm_ProblemTitles_PlainAndDistinct()
    {
        var titles = ProfilesForm.ProblemTitles([
            "work: skills is a real folder, not shared (run 'clauderoo share work skills')",
            "work: skills is a real folder, not shared (run 'clauderoo share work skills')",
        ]);
        Assert.Equal(["work keeps its own copy of skills, so it isn't shared"], titles);
    }

    static UsageReading Usage(DateTimeOffset at, params UsageWindow[] w) => new("work", at, "Max 5x", w);

    static UsageWindow W(string name, double percent, DateTimeOffset? resets = null) => new(name, percent, resets);

    [Fact]
    public void UsageSuffix_Format()
    {
        var at = DateTimeOffset.Now;
        Assert.Equal(" · 5h 42% · 7d 63%", TrayText.UsageSuffix(Usage(at, W("5h", 42.4), W("7d", 62.5), W("Opus", 71))));
        Assert.Equal(" · 7d 63%", TrayText.UsageSuffix(Usage(at, W("7d", 63))));
        Assert.Equal("", TrayText.UsageSuffix(Usage(at, W("Opus", 71))));
        Assert.Equal("", TrayText.UsageSuffix(null));
    }

    [Fact]
    public void UsageRows_Format()
    {
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var today = new DateTimeOffset(DateTime.Today.AddHours(15).AddMinutes(40));
        var later = now.AddDays(3);
        var r = Usage(now, W("5h", 42, today), W("7d", 63, later), W("Opus", 71));
        Assert.Equal(
        [
            "5-hour: 42%, resets 15:40",
            $"7-day: 63%, resets {later.LocalDateTime.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}",
            "Opus: 71%",
        ], TrayText.UsageRows(r, new UsageResult.Ok(r), now));

        // a stale reading kept after a failed fetch: "as of", then why
        var old = Usage(now.AddMinutes(-40), W("5h", 42));
        Assert.Equal(["5-hour: 42%", $"As of {now.AddMinutes(-40).LocalDateTime:HH:mm}", "Usage is rate limited; clauderoo will try again later."],
            TrayText.UsageRows(old, new UsageResult.RateLimited(TimeSpan.FromMinutes(10)), now));

        Assert.Equal(["Sign-in expired. Start a session of this profile to refresh it."],
            TrayText.UsageRows(null, new UsageResult.Expired(), now));
        Assert.Empty(TrayText.UsageRows(null, null, now));
        Assert.Empty(TrayText.UsageRows(null, new UsageResult.NotSignedIn(), now));
    }

    [Fact]
    public void UsageInfo_ShowsWhenLastChecked_WhenNoNewReading()
    {
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var r = Usage(now.AddHours(-2), W("5h", 42));
        string T(DateTimeOffset t) => t.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // a fresh reading: the check is the reading, no extra time
        Assert.Equal($"Plan: Max 5x · as of {T(now.AddHours(-2))}", TrayText.UsageInfo(r, now.AddHours(-2), true, now));
        // checked since without a new reading (expired sign-in): both times, so Refresh visibly did something
        Assert.Equal($"Plan: Max 5x · as of {T(now.AddHours(-2))} · checked {T(now)}", TrayText.UsageInfo(r, now, true, now));
        Assert.Equal($"No reading yet · checked {T(now)}", TrayText.UsageInfo(null, now, true, now));
        Assert.Equal("Not checked yet.", TrayText.UsageInfo(null, null, true, now));
        Assert.Equal("Not signed in — start a session to sign in.", TrayText.UsageInfo(null, now, false, now));
    }

    [Fact]
    public void UsageLevel_Thresholds()
    {
        Assert.Equal(UsageLevel.Ok, TrayText.UsageLevelOf(69.9));
        Assert.Equal(UsageLevel.Warn, TrayText.UsageLevelOf(70));
        Assert.Equal(UsageLevel.Warn, TrayText.UsageLevelOf(89.9));
        Assert.Equal(UsageLevel.High, TrayText.UsageLevelOf(90));
    }

    [Fact]
    public void UsageNotTracked_NamesProvider() =>
        Assert.Equal("Usage isn't tracked for Google Vertex AI.", TrayText.UsageNotTracked(Providers.Vertex));

    // ---------- auto-switch ----------

    [Fact]
    public void AutoSwitchBalloon_Text() =>
        Assert.Equal("personal is at 92% of its 5-hour limit. New terminals now use work.",
            TrayText.AutoSwitchBalloon("personal is at 92% of its 5-hour limit.", "work"));

    [Fact]
    public void RecoveredBalloon_Text() =>
        Assert.Equal("personal has room again. Click here to make it the default.", TrayText.RecoveredBalloon("personal"));

    [Fact]
    public void NoTargetBalloon_Text() =>
        Assert.Equal("personal is at 92% of its 5-hour limit. No other profile has room, so the default stays personal.",
            TrayText.NoTargetBalloon("personal is at 92% of its 5-hour limit.", "personal"));

    [Fact]
    public void AutoSwitchMenu_LabelShowsThreshold()
    {
        Assert.Equal("&Auto-switch at 90% usage", TrayText.AutoSwitchMenu(null));
        Assert.Equal("&Auto-switch at 85% usage", TrayText.AutoSwitchMenu(new AutoSwitchSettings { Threshold = 85 }));
    }

    [Fact]
    public void AutoSwitchToggled_Text()
    {
        Assert.Equal("Auto-switch is on. When the default profile reaches 85% of a limit, new terminals use another profile with room.",
            TrayText.AutoSwitchToggled(new AutoSwitchSettings { Enabled = true, Threshold = 85 }));
        Assert.Equal("Auto-switch is off.", TrayText.AutoSwitchToggled(new AutoSwitchSettings { Threshold = 85 }));
    }

    [Fact]
    public void WarnThreshold_IsTheAutoSwitchThreshold_OnlyWhenOn()
    {
        Assert.Equal(70, TrayText.WarnThreshold(null));
        Assert.Equal(70, TrayText.WarnThreshold(new AutoSwitchSettings { Enabled = false, Threshold = 85 }));
        Assert.Equal(85, TrayText.WarnThreshold(new AutoSwitchSettings { Enabled = true, Threshold = 85 }));
    }

    [Fact]
    public void UsageSuffixParts_FlagWindowsAtOrAboveThreshold()
    {
        var at = DateTimeOffset.Now;
        var parts = TrayText.UsageSuffixParts(Usage(at, W("5h", 79.9), W("7d", 80), W("Opus", 99)), threshold: 80);
        Assert.Equal([(" · ", UsageLevel.Ok), ("5h 80%", UsageLevel.Ok), (" · ", UsageLevel.Ok), ("7d 80%", UsageLevel.Warn)], parts);

        // red from 90 %, whatever the threshold
        parts = TrayText.UsageSuffixParts(Usage(at, W("5h", 95), W("7d", 10)), threshold: 90);
        Assert.Equal([(" · ", UsageLevel.Ok), ("5h 95%", UsageLevel.High), (" · ", UsageLevel.Ok), ("7d 10%", UsageLevel.Ok)], parts);

        Assert.Empty(TrayText.UsageSuffixParts(null, 90));
        // the parts read the same as the plain suffix
        var r = Usage(at, W("5h", 42.4), W("7d", 91));
        Assert.Equal(TrayText.UsageSuffix(r), string.Concat(TrayText.UsageSuffixParts(r, 90).Select(p => p.Text)));
    }

    [Fact]
    public void AutoSwitchSummary_Text()
    {
        Assert.Equal("Auto-switch is off.", TrayText.AutoSwitchSummary(new AutoSwitchSettings { Threshold = 85 }));
        Assert.Equal("Auto-switch is on at 85%. Order: work, personal.",
            TrayText.AutoSwitchSummary(new AutoSwitchSettings { Enabled = true, Threshold = 85, Order = ["work", "personal"] }));
        Assert.Equal("Auto-switch is on at 90%. Order: every Claude-login profile, in list order.",
            TrayText.AutoSwitchSummary(new AutoSwitchSettings { Enabled = true }));
    }

    [Fact]
    public void AutoSwitchOrder_Items_ListedFirstThenTheRest()
    {
        var items = AutoSwitchOrder.Items(["work", "gone", "api"],
            [("personal", Providers.Login), ("api", Providers.ApiKey), ("work", Providers.Login), ("vx", Providers.Vertex)]);
        Assert.Equal([("work", true), ("api", true), ("personal", false), ("vx", false)], items);
        Assert.Equal(["work", "api"], AutoSwitchOrder.ToOrder(items));
        Assert.Empty(AutoSwitchOrder.ToOrder([("personal", false)]));
    }

    [Fact]
    public void AutoSwitchOrder_Move()
    {
        var items = new List<(string, bool)> { ("a", true), ("b", false), ("c", true) };
        Assert.Equal(0, AutoSwitchOrder.Move(items, 1, -1));
        Assert.Equal([("b", false), ("a", true), ("c", true)], items);
        Assert.Equal(0, AutoSwitchOrder.Move(items, 0, -1)); // already first: unchanged
        Assert.Equal(2, AutoSwitchOrder.Move(items, 1, +1));
        Assert.Equal([("b", false), ("c", true), ("a", true)], items);
    }

    [Fact]
    public void TokenProfile_UsageNotTracked_InTrayAndWindow()
    {
        var token = new ProfileInfo("ci", false, false, null, [], @"C:\x\ci", Providers.Token);
        // Never polled (the poller's targets are the tracked profiles) ...
        Assert.False(TrayMenuState.TracksUsage(token));
        // ... and the window's Usage group and the tray submenu say why, in the same words as the CLI.
        Assert.Equal("Usage isn't tracked for Claude token.", TrayText.UsageNotTrackedFor(token));
        Assert.Equal("Usage isn't tracked for Claude token.", new UsageResult.NotTracked(Providers.Token).Message);
        Assert.Null(TrayText.UsageNotTrackedFor(Login("work", "w@example.com")));
        Assert.Null(TrayText.UsageNotTrackedFor(Login("personal", null, home: true)));

        // A reading stored under its name (say, from before it became a token profile) isn't shown.
        var now = DateTimeOffset.Now;
        var stale = Usage(now, W("5h", 42));
        using var menu = new ContextMenuStrip();
        var state = new TrayMenuState([Login("work", "w@example.com", isDefault: true), token], [],
            new Dictionary<string, int>(), false,
            Usage: new Dictionary<string, UsageReading> { ["ci"] = stale with { Profile = "ci" } }, Now: now);
        TrayMenu.Build(menu, state, new TrayMenuActions());
        var item = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text!.StartsWith("ci"));
        Assert.DoesNotContain("42%", item.Text);
        var rows = item.DropDownItems.OfType<ToolStripMenuItem>().Where(i => !i.Enabled).Select(i => i.Text).ToList();
        Assert.Contains("Usage isn't tracked for Claude token.", rows);
        Assert.DoesNotContain(rows, r => r!.Contains("42%"));
    }
}