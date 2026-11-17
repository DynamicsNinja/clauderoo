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
    public void Label_Login_WithEmail()
    {
        Assert.Equal("personal (home) — Claude login · someone@example.com",
            TrayText.ProfileLabel(Login("personal", "someone@example.com", home: true)));
    }

    [Fact]
    public void Label_Login_NotSignedIn()
    {
        var p = Login("plain", null);
        Assert.Equal("plain — Claude login · not signed in", TrayText.ProfileLabel(p));
        Assert.True(TrayText.IsSignedOut(p));
    }

    [Fact]
    public void Label_Vertex_And_ApiKey()
    {
        Assert.Equal("work — Google Vertex AI · acme-prod", TrayText.ProfileLabel(Vertex("work", "acme-prod")));
        Assert.Equal("work — Google Vertex AI", TrayText.ProfileLabel(Vertex("work", null)));
        Assert.Equal("api — API key", TrayText.ProfileLabel(ApiKey("api")));
        Assert.False(TrayText.IsSignedOut(ApiKey("api")));
    }

    [Fact]
    public void ProviderLabel_Token_IsClaudeToken()
    {
        var p = new ProfileInfo("ci", false, false, null, [], @"C:\x\ci", Providers.Token);
        Assert.Equal("Claude token", TrayText.ProviderName(Providers.Token));
        Assert.Equal("ci — Claude token", TrayText.ProfileLabel(p));
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
}