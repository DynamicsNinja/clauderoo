using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// The Sharing window (#4): it shows ProfileManager.SharingOf and calls Own / Share / SetExclusions, nothing more.
[Collection(WinFormsCollection.Name)]
public class SharingDialogTests
{
    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => new DateTime(2026, 11, 18));
        pm.Init();
        var skills = Path.Combine(home.Paths.DefaultHomeDir, "skills");
        Directory.CreateDirectory(Path.Combine(skills, "a"));
        Directory.CreateDirectory(Path.Combine(skills, "b"));
        pm.Add("work");
        return (pm, home);
    }

    private static Task<SyncReport> Inline(Func<SyncReport> w) => Task.FromResult(w());

    [Fact]
    public void Rows_ShowEachItemsState()
    {
        var (pm, home) = Make();
        using var _ = home;
        pm.Exclude("work", "skills/a");
        pm.Own("work", "CLAUDE.md");
        using var dlg = new SharingDialog(pm, "work", Inline);

        var rows = dlg.RowsForTest;
        Assert.Contains("skills | Folder | Shared, leaving out 1 | a", rows);
        Assert.Contains("CLAUDE.md | File | Own copy | Not updated from home", rows);
        Assert.Contains("mcpServers | MCP servers | Shared from home | ", rows);
        Assert.Contains("plugins | Folder | Shared from home | ", rows);
    }

    [Fact]
    public void Buttons_FollowTheSelectedItem()
    {
        var (pm, home) = Make();
        using var _ = home;
        pm.Own("work", "CLAUDE.md");
        using var dlg = new SharingDialog(pm, "work", Inline);

        dlg.SelectForTest("skills");
        Assert.Equal((true, false, true), dlg.ButtonsForTest);
        dlg.SelectForTest("CLAUDE.md");
        Assert.Equal((false, true, false), dlg.ButtonsForTest);
        dlg.SelectForTest("settings.json");
        Assert.Equal((true, false, false), dlg.ButtonsForTest);
    }

    [Fact]
    public async Task Own_GoesThroughTheWriter_AndRefreshes()
    {
        var (pm, home) = Make();
        using var _ = home;
        var writes = 0;
        using var dlg = new SharingDialog(pm, "work", w => { writes++; return Task.FromResult(w()); });
        dlg.SelectForTest("skills");

        await dlg.OwnForTest();

        Assert.Equal(1, writes);
        Assert.Contains("skills", pm.Get("work").Overrides);
        Assert.Contains("skills | Folder | Own copy | Not updated from home", dlg.RowsForTest);
        Assert.Contains("work now has its own skills.", dlg.ResultForTest);
    }

    [Fact]
    public async Task FailedWrite_ShowsTheMessage()
    {
        var (pm, home) = Make();
        using var _ = home;
        using var dlg = new SharingDialog(pm, "work", Inline);
        await dlg.RunForTest("plugins", () => throw new ClauderooException("busy right now"), "done");
        Assert.Equal("busy right now", dlg.ResultForTest);
    }

    [Fact]
    public void Picker_UntickedAreExcluded_MissingOnesListed()
    {
        using var p = ExclusionPicker.CreateForTest("skills", ["a", "b"], ["b", "gone"]);
        var list = p.ListForTest;
        Assert.Equal(3, list.Items.Count);
        Assert.True(list.GetItemChecked(0));
        Assert.False(list.GetItemChecked(1));
        Assert.Equal("gone  (not in home)", list.Items[2]);
        Assert.Equal(["b", "gone"], p.Excluded);

        list.SetItemChecked(1, true);
        list.SetItemChecked(2, true);
        list.SetItemChecked(0, false);
        Assert.Equal(["a"], p.Excluded);
    }

    [Fact]
    public void Text_PluginsSayTurnedOff()
    {
        var s = new SharedItemStatus("plugins", SharedKind.Folder, ShareState.Excluding, ["x@m", "y@m"], true, true);
        Assert.Equal("Shared, 2 plugins turned off", SharingText.State(s));
        Assert.Equal("x@m, y@m", SharingText.Detail(s));
    }

    [Fact]
    public void ProfilesForm_HasTheSharingButton()
    {
        var (pm, home) = Make();
        using var _ = home;
        using var form = new ProfilesForm(pm, _ => { });
        Assert.Equal("Sharin&g…", form.SharingButtonForTest.Text);
    }
}
