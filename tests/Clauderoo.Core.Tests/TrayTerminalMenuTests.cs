using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class TrayTerminalMenuTests
{
    static ProfileInfo Login(string name, bool isDefault = false) =>
        new(name, false, isDefault, name + "@example.com", [], @"C:\x\" + name, Providers.Login);

    static ToolStripMenuItem Advanced(ContextMenuStrip menu) =>
        menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == TrayMenu.SettingsMenu);

    [Fact]
    public void ProfileSubmenu_HasOpenInVsCode()
    {
        string? opened = null;
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu, new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false),
            new TrayMenuActions { OpenInCode = n => opened = n });
        var profile = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text!.StartsWith("work"));
        var code = Assert.Single(profile.DropDownItems.OfType<ToolStripMenuItem>(), i => i.Text == "Open in &VS Code…");
        Assert.False(string.IsNullOrEmpty(code.ToolTipText));
        code.PerformClick();
        Assert.Equal("work", opened);
    }

    [Fact]
    public void Advanced_SessionsOpenIn_ShowsAndSetsTheChoice()
    {
        string? terminal = null, shell = null;
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu,
            new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false,
                Terminal: TerminalChoice.Conhost, Shell: TerminalChoice.Pwsh),
            new TrayMenuActions { SetTerminal = t => terminal = t, SetShell = s => shell = s });
        var open = Advanced(menu).DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "New &sessions open in");
        var items = open.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        Assert.Equal(new[] { "&Windows Terminal", "Console wi&ndow", "&Command Prompt", "PowerShell &7", "Windows &PowerShell" },
            items.Select(i => i.Text));
        Assert.Equal(new[] { false, true, false, true, false }, items.Select(i => i.Checked));

        items[1].PerformClick(); // already chosen: nothing to do
        Assert.Null(terminal);
        items[0].PerformClick();
        Assert.Equal(TerminalChoice.Wt, terminal);
        items[4].PerformClick();
        Assert.Equal(TerminalChoice.PowerShell, shell);
    }

    [Fact]
    public void Advanced_CheckForUpdatesNow_OnlyInReleaseBuilds()
    {
        var clicked = 0;
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu,
            new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false, UpdateChecksAvailable: true),
            new TrayMenuActions { CheckUpdatesNow = () => clicked++ });
        var items = Advanced(menu).DropDownItems.OfType<ToolStripMenuItem>().ToList();
        Assert.Contains(items, i => i.Text == "Check for &updates automatically");
        items.Single(i => i.Text == "Check for updates &now…").PerformClick();
        Assert.Equal(1, clicked);

        using var dev = new ContextMenuStrip();
        TrayMenu.Build(dev, new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false),
            new TrayMenuActions());
        Assert.DoesNotContain(Advanced(dev).DropDownItems.OfType<ToolStripMenuItem>(), i => i.Text!.StartsWith("Check for updates"));
    }

    [Fact]
    public void SettingItems_StayOpen_AndShowTheChangeInPlace()
    {
        bool? autoStart = null, updates = null;
        var openWhileRunning = new List<bool>();
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu,
            new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false, UpdateChecksAvailable: true),
            new TrayMenuActions
            {
                SetAutoStart = on => { autoStart = on; openWhileRunning.Add(TrayMenu.StaysOpen(ToolStripDropDownCloseReason.ItemClicked)); },
                SetUpdateChecks = on => updates = on,
                CheckThisPc = () => openWhileRunning.Add(TrayMenu.StaysOpen(ToolStripDropDownCloseReason.ItemClicked)),
            });
        var adv = Advanced(menu).DropDownItems.OfType<ToolStripMenuItem>().ToList();

        var start = adv.Single(i => i.Text == "Start with &Windows");
        start.PerformClick();
        Assert.True(start.Checked);
        Assert.True(autoStart);
        start.PerformClick();
        Assert.False(start.Checked);
        Assert.False(autoStart);

        var checks = adv.Single(i => i.Text == "Check for &updates automatically");
        Assert.True(checks.Checked);
        checks.PerformClick();
        Assert.False(checks.Checked);
        Assert.False(updates);

        // A window-opening item still closes the menu; only the setting toggles keep it open.
        adv.Single(i => i.Text!.StartsWith("&Check this PC")).PerformClick();
        Assert.Equal([true, true, false], openWhileRunning);
        Assert.False(TrayMenu.StaysOpen(ToolStripDropDownCloseReason.AppClicked)); // clicking elsewhere always closes
    }

    [Fact]
    public void SessionsOpenIn_MovesTheTickInPlace()
    {
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu,
            new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false,
                Terminal: TerminalChoice.Wt, Shell: TerminalChoice.Cmd),
            new TrayMenuActions());
        var open = Advanced(menu).DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "New &sessions open in");
        var items = open.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        items[1].PerformClick(); // Console window
        items[3].PerformClick(); // PowerShell 7
        Assert.Equal(new[] { false, true, false, true, false }, items.Select(i => i.Checked));
    }

    [Fact]
    public void RefreshUsage_StaysOpen_AndSaysItIsRefreshing()
    {
        var refreshed = 0;
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu, new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false),
            new TrayMenuActions { RefreshUsage = () => refreshed++ });
        var refresh = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Refresh &usage");
        refresh.PerformClick();
        Assert.Equal(1, refreshed);
        Assert.Equal("Refreshing usage…", refresh.Text);
        Assert.False(refresh.Enabled);
    }

    [Fact]
    public void Menu_StartsWithStatus_ThenUpdate_ThenStartDefault()
    {
        string? started = null;
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu, new TrayMenuState([Login("personal", isDefault: true), Login("work")], [], new Dictionary<string, int>(), false,
            UpdateVersion: "9.9.9", UpdateChecksAvailable: true), new TrayMenuActions { Start = n => started = n });
        var texts = menu.Items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "---" : i.Text).ToList();
        Assert.Equal("clauderoo — all good", texts[0]);
        Assert.Equal("&Update to v9.9.9", texts[1]);
        Assert.Equal("---", texts[2]);
        Assert.Equal("&Start personal", texts[3]);
        Assert.StartsWith("personal · ", texts[4]);
        Assert.Equal("E&xit clauderoo", texts[^1]);
        Assert.DoesNotContain(texts, t => t == "&Advanced");
        menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "&Start personal").PerformClick();
        Assert.Equal("personal", started);
    }

    [Fact]
    public void Settings_HasATroubleshootGroup_AtTheEnd()
    {
        using var menu = new ContextMenuStrip();
        TrayMenu.Build(menu, new TrayMenuState([Login("work", isDefault: true)], [], new Dictionary<string, int>(), false),
            new TrayMenuActions());
        var items = Advanced(menu).DropDownItems.Cast<ToolStripItem>().ToList();
        var trouble = items.FindIndex(i => i.Text == "Troubleshoot");
        Assert.True(trouble > 0);
        Assert.IsType<ToolStripSeparator>(items[trouble - 1]);
        Assert.Equal(new[] { "&Check this PC…", "&Open profiles folder" }, items.Skip(trouble + 1).Select(i => i.Text));
    }

    [Fact]
    public void UsageColours_OneRule_ForMenuAndWindow()
    {
        Assert.Equal(UsageLevel.Ok, TrayText.UsageLevelOf(69));
        Assert.Equal(UsageLevel.Warn, TrayText.UsageLevelOf(78));
        Assert.Equal(UsageLevel.High, TrayText.UsageLevelOf(90));
        // The menu's parts use the same function: 78 % is amber there too.
        var r = new UsageReading("p", DateTimeOffset.Now, null, [new UsageWindow("5h", 78, null)]);
        Assert.Contains(("5h 78%", UsageLevel.Warn), TrayText.UsageSuffixParts(r, TrayText.WarnThreshold(null)));
        Assert.StartsWith("Refresh &usage (as of ", TrayText.RefreshUsage(DateTimeOffset.Now));
    }
}