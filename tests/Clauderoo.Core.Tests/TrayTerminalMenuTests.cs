using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class TrayTerminalMenuTests
{
    static ProfileInfo Login(string name, bool isDefault = false) =>
        new(name, false, isDefault, name + "@example.com", [], @"C:\x\" + name, Providers.Login);

    static ToolStripMenuItem Advanced(ContextMenuStrip menu) =>
        menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "&Advanced");

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
}
