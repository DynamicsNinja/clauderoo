using System.Reflection;
using System.Windows.Forms;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// The Profiles window's write-gate follow-ups, driven through its private members (the window has no seams for them).
public class ProfilesFormMinorTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static (ProfileManager pm, TempHome home) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => DateTime.Now);
        pm.Init();
        pm.Add("work");
        pm.SetDefault("personal");
        return (pm, home);
    }

    private static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, Flags)!.GetValue(o)!;
    private static void Set(object o, string field, object? value) => o.GetType().GetField(field, Flags)!.SetValue(o, value);
    private static void Call(object o, string method) => o.GetType().GetMethod(method, Flags)!.Invoke(o, null);

    [Fact]
    public void UndoRemove_GateBusy_KeepsUndoLink_ThenRetries()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.Remove("work");
        var gate = new WriteGate();
        using var form = new ProfilesForm(pm, _ => { }, gate: gate);
        Set(form, "_undoName", "work");
        Set(form, "_resultLink", (Action)(() => Call(form, "UndoRemove")));

        using (gate.TryEnter())
        {
            var e = Assert.Throws<TargetInvocationException>(() => Call(form, "UndoRemove"));
            Assert.Equal(TrayText.Busy, Assert.IsType<ClauderooException>(e.InnerException).Message);
            // Not restored, and Undo is still offered.
            Assert.False(pm.Config.Profiles.ContainsKey("work"));
            Assert.Equal("work", Get<string?>(form, "_undoName"));
            Assert.NotNull(Get<Action?>(form, "_resultLink"));
        }

        Call(form, "UndoRemove"); // the retry goes through
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
        Assert.Null(Get<string?>(form, "_undoName"));
    }

    [Fact]
    public void RefreshList_Dirty_RefreshesOnlyTheDefaultMarker()
    {
        var (pm, home) = Make();
        using var _h = home;
        using var form = new ProfilesForm(pm, _ => { }, gate: new WriteGate());
        form.Reload("work");
        var env = Get<DataGridView>(form, "_env");
        env.Rows.Add("MY_VAR", "1"); // an unsaved edit
        Assert.True(Get<Button>(form, "_setDefault").Enabled);

        pm.SetDefault("work"); // e.g. auto-switch, or the tray's own Set default
        form.RefreshList();

        // The unsaved edit is still there ...
        Assert.Contains(env.Rows.Cast<DataGridViewRow>(), r => !r.IsNewRow && (string?)r.Cells[0].Value == "MY_VAR");
        // ... and the star moved: work is the default, personal isn't, and Set default is off for work.
        var list = Get<ListBox>(form, "_list");
        var infos = list.Items.Cast<object>()
            .Select(i => (ProfileInfo)i.GetType().GetProperty("Info")!.GetValue(i)!).ToDictionary(i => i.Name);
        Assert.True(infos["work"].IsDefault);
        Assert.False(infos["personal"].IsDefault);
        Assert.True(Get<bool>(form, "_currentIsDefault"));
        Assert.False(Get<Button>(form, "_setDefault").Enabled);
    }
}
