using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

[Collection(WinFormsCollection.Name)]
public class ProfilesLayoutTests
{
    static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0);

    static (ProfileManager, TempHome) Make()
    {
        var home = new TempHome();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(), new FakeSecretStore())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home);
    }

    static IEnumerable<T> All<T>(Control c) where T : Control =>
        c.Controls.Cast<Control>().SelectMany(x => (x is T t ? new[] { t } : []).Concat(All<T>(x)));

    static TabControl Tabs(Form f) => All<TabControl>(f).Single();
    static string[] Captions(Form f) => Tabs(f).TabPages.Cast<TabPage>().Select(p => p.Text).ToArray();

    [Fact]
    public void Home_HasNoEnvironmentTab_OthersHaveAllFour_WithCounts()
    {
        var (pm, home) = Make();
        using var _h = home;
        var dir = Path.Combine(home.Paths.Root, "proj");
        Directory.CreateDirectory(dir);
        pm.Map("work", dir);
        pm.SetEnv("work", [new("A", "1"), new("B", "2")], []);
        using var form = new ProfilesForm(pm, _ => { });
        form.Reload("personal");
        Assert.Equal(new[] { "Account", "Folders", "Sharing" }, Captions(form));
        form.Reload("work");
        Assert.Equal(new[] { "Account", "Folders (1)", "Environment (2)", "Sharing" }, Captions(form));
    }

    [Fact]
    public void MenuBar_HoldsTheListAndAppActions()
    {
        var (pm, home) = Make();
        using var _h = home;
        using var form = new ProfilesForm(pm, _ => { });
        var menu = Assert.IsType<MenuStrip>(form.MainMenuStrip);
        var items = menu.Items.OfType<ToolStripMenuItem>().SelectMany(m => m.DropDownItems.OfType<ToolStripMenuItem>()).Select(i => i.Text).ToList();
        foreach (var want in new[] { "&Duplicate…", "&Import…", "E&xport…", "&Restore removed profiles…", "&Auto-switch…", "&Connect Claude to clauderoo (MCP)…" })
            Assert.Contains(want, items);
        // Under the list: only the three row actions.
        Assert.DoesNotContain(All<Button>(form), b => b.Text is "E&xport…" or "Import…" or "Removed…" or "Duplicate…");
    }

    [Fact]
    public void Vertex_ModelsAreFolded_UnlessOneIsSet()
    {
        var (pm, home) = Make();
        using var _h = home;
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "p", Region = "r" });
        using var form = new ProfilesForm(pm, _ => { });
        form.Show();
        form.Reload("work");
        var opus = All<Label>(form).Single(l => l.Text == "Op&us model:");
        Assert.False(opus.Visible);
        Assert.StartsWith("▸", All<LinkLabel>(form).Single(l => l.Text.EndsWith("Models (optional)")).Text);
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "p", Region = "r", OpusModel = "claude-opus-5-5" });
        form.Reload("work");
        Assert.True(opus.Visible);
        form.Close();
    }

    [Fact]
    public void SignedOut_ShowsNoUsage_AndSaysItOnce()
    {
        var (pm, home) = Make();
        using var _h = home;
        var view = new UsageView();
        var at = DateTimeOffset.Now;
        view.Seed(new Dictionary<string, UsageReading> { ["work"] = new("work", at, "pro", [new UsageWindow("5h", 50, null)]) });
        using var form = new ProfilesForm(pm, _ => { }, usage: view);
        form.Show();
        form.Reload("work"); // never signed in
        Assert.DoesNotContain(All<Control>(form), c => c.Visible && c.AccessibleRole == AccessibleRole.ProgressBar);
        var notSignedIn = All<Control>(form).Count(c => c.Visible && c is not ListBox && c.Text.Contains("ot signed in", StringComparison.Ordinal));
        Assert.True(notSignedIn == 1, string.Join(" | ", All<Control>(form).Where(c => c.Visible && c is not ListBox && c.Text.Contains("ot signed in", StringComparison.Ordinal)).Select(c => c.GetType().Name + ":" + c.Text)));
        form.Close();
    }
}
