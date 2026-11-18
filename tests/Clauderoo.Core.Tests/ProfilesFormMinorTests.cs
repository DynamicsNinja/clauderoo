using System.Windows.Forms;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// The Profiles window's write-gate follow-ups, driven through its internal test accessors.
[Collection(WinFormsCollection.Name)]
public class ProfilesFormMinorTests
{
    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        pm.Add("work");
        pm.SetDefault("personal");
        return (pm, home);
    }

    [Fact]
    public void UndoRemove_GateBusy_KeepsUndoLink_ThenRetries()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Remove("work");
        var gate = new WriteGate();
        using var form = new ProfilesForm(pm, _ => { }, gate: gate);
        form.OfferUndoForTest("work", form.UndoRemoveForTest);

        using (gate.TryEnter())
        {
            var e = Assert.Throws<ClauderooException>(form.UndoRemoveForTest);
            Assert.Equal(TrayText.Busy, e.Message);
            // Not restored, and Undo is still offered.
            Assert.False(pm.Config.Profiles.ContainsKey("work"));
            Assert.Equal("work", form.UndoNameForTest);
            Assert.NotNull(form.ResultLinkForTest);
        }

        form.UndoRemoveForTest(); // the retry goes through
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
        Assert.Null(form.UndoNameForTest);
    }

    [Fact]
    public void RefreshList_Dirty_RefreshesOnlyTheDefaultMarker()
    {
        var (pm, home) = Make();
        using var _h = home;
        using var form = new ProfilesForm(pm, _ => { }, gate: new WriteGate());
        form.Reload("work");
        var env = form.EnvGridForTest;
        env.Rows.Add("MY_VAR", "1"); // an unsaved edit
        Assert.True(form.SetDefaultButtonForTest.Enabled);

        pm.SetDefault("work"); // e.g. auto-switch, or the tray's own Set default
        form.RefreshList();

        // The unsaved edit is still there ...
        Assert.Contains(env.Rows.Cast<DataGridViewRow>(), r => !r.IsNewRow && (string?)r.Cells[0].Value == "MY_VAR");
        // ... and the star moved: work is the default, personal isn't, and Set default is off for work.
        var infos = form.ListedProfilesForTest.ToDictionary(i => i.Name);
        Assert.True(infos["work"].IsDefault);
        Assert.False(infos["personal"].IsDefault);
        Assert.True(form.CurrentIsDefaultForTest);
        Assert.False(form.SetDefaultButtonForTest.Enabled);
    }
}
