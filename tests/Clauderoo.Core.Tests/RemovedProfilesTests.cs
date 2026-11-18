using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class RemovedProfilesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0);

    private sealed class Rig : IDisposable
    {
        public readonly TempHome Home = new();
        public DateTime Now = T0;
        public readonly ProfileManager Pm;

        public Rig()
        {
            Pm = new ProfileManager(Home.Paths, new FakeEnv(), () => Now);
            Pm.Init();
        }

        public ClauderooPaths Paths => Home.Paths;

        /// <summary>Adds <paramref name="name"/> with a marker file and removes it at <paramref name="at"/>.</summary>
        public void AddAndRemove(string name, DateTime at, string marker = "m")
        {
            Now = at;
            Pm.Add(name);
            File.WriteAllText(Path.Combine(Paths.ProfileDir(name), "marker.txt"), marker);
            Pm.Remove(name);
        }

        public string Copy(string name, DateTime at) =>
            Path.Combine(Paths.RemovedDir, $"{name}-{at:yyyyMMdd-HHmmss}");

        public void Dispose() => Home.Dispose();
    }

    // ---------- SafeTree: the one real delete never follows a junction ----------

    [Fact]
    public void SafeTreeDelete_JunctionToFolderWithFiles_TargetUntouched()
    {
        using var h = new TempHome();
        var target = Path.Combine(h.Dir, "target");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "keep.txt"), "k");
        File.WriteAllText(Path.Combine(target, "sub", "deep.txt"), "d");
        var tree = Path.Combine(h.Dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
        File.WriteAllText(Path.Combine(tree, "a", "own.txt"), "o");
        Junction.Create(Path.Combine(tree, "link"), target);          // top level
        Junction.Create(Path.Combine(tree, "a", "b", "link2"), target); // nested

        SafeTree.Delete(tree);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("k", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Equal("d", File.ReadAllText(Path.Combine(target, "sub", "deep.txt")));
    }

    [Fact]
    public void SafeTreeDelete_RootIsJunction_RemovesOnlyTheLink()
    {
        using var h = new TempHome();
        var target = Path.Combine(h.Dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "k");
        var link = Path.Combine(h.Dir, "link");
        Junction.Create(link, target);

        SafeTree.Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void SafeTreeDelete_ReadOnlyAndHiddenFiles_Deleted()
    {
        using var h = new TempHome();
        var tree = Path.Combine(h.Dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "objects"));
        var ro = Path.Combine(tree, "objects", "pack.idx");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var hidden = Path.Combine(tree, ".hidden");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        SafeTree.Delete(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void SafeTreeDelete_DanglingJunction_Removed()
    {
        using var h = new TempHome();
        var tree = Path.Combine(h.Dir, "tree");
        Directory.CreateDirectory(tree);
        var gone = Path.Combine(h.Dir, "gone");
        Directory.CreateDirectory(gone);
        Junction.Create(Path.Combine(tree, "link"), gone);
        Directory.Delete(gone);

        SafeTree.Delete(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void SafeTreeSize_CountsOwnFilesOnly()
    {
        using var h = new TempHome();
        var target = Path.Combine(h.Dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[5000]);
        var tree = Path.Combine(h.Dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllBytes(Path.Combine(tree, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tree, "sub", "b.bin"), new byte[23]);
        Junction.Create(Path.Combine(tree, "link"), target);

        Assert.Equal(123, SafeTree.Size(tree));
    }

    // ---------- ListRemoved ----------

    [Fact]
    public void ListRemoved_ListsCopies_NewestFirst_WithTimeAndSize()
    {
        using var r = new Rig();
        var t1 = T0;
        var t2 = T0.AddDays(3);
        r.AddAndRemove("work", t1, "old");
        r.AddAndRemove("work", t2, "newer!");
        r.AddAndRemove("alpha", t2);
        Directory.CreateDirectory(Path.Combine(r.Paths.RemovedDir, "not-ours")); // ignored

        var list = r.Pm.ListRemoved();

        Assert.Equal(["alpha", "work", "work"], list.Select(c => c.Name));
        var newest = list[1];
        Assert.Equal("20261104-090000", newest.Id);
        Assert.Equal(t2, newest.RemovedAt);
        Assert.Equal(r.Copy("work", t2), newest.ProfileDir);
        Assert.True(newest.Size >= "newer!".Length);
        Assert.Equal("20261101-090000", list[2].Id);
        Assert.Equal(["work", "work"], r.Pm.ListRemoved("work").Select(c => c.Name));
    }

    [Fact]
    public void ListRemoved_PairsIsolatedFolder()
    {
        using var r = new Rig();
        r.Now = T0;
        r.Pm.Add("work");
        r.Pm.PrepareIsolated("work");
        r.Pm.Remove("work");

        var c = Assert.Single(r.Pm.ListRemoved());
        Assert.NotNull(c.ProfileDir);
        Assert.Equal(r.Copy("work", T0) + "-isolated", c.IsolatedDir);
    }

    [Fact]
    public void ListRemoved_NoRemovedDir_Empty()
    {
        using var r = new Rig();
        Assert.Empty(r.Pm.ListRemoved());
    }

    // ---------- Restore a chosen copy ----------

    [Fact]
    public void Restore_At_PicksThatCopy_LeavesTheOthers()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0, "old");
        r.AddAndRemove("work", T0.AddDays(1), "new");

        var report = r.Pm.Restore("work", "20261101-090000");

        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Paths.ProfileDir("work"), "marker.txt")));
        Assert.True(Directory.Exists(r.Copy("work", T0.AddDays(1))));
        Assert.Contains(report.Notices, n => n.Contains("1 other removed copy") && n.Contains("clauderoo removed"));
    }

    [Fact]
    public void Restore_AtAcceptsFolderName()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0, "old");
        r.AddAndRemove("work", T0.AddDays(1), "new");
        r.Pm.Restore("work", "work-20261101-090000");
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Paths.ProfileDir("work"), "marker.txt")));
    }

    [Fact]
    public void Restore_AtUnknown_Refused()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var e = Assert.Throws<ClauderooException>(() => r.Pm.Restore("work", "20200101-000000"));
        Assert.Contains("clauderoo removed", e.Message);
        Assert.Throws<ClauderooException>(() => r.Pm.Restore("work", "yesterday"));
    }

    [Fact]
    public void Restore_BringsIsolatedFolderBack()
    {
        using var r = new Rig();
        r.Pm.Add("work");
        var (iso, _) = r.Pm.PrepareIsolated("work");
        File.WriteAllText(Path.Combine(iso, "login.txt"), "x");
        r.Pm.Remove("work");

        r.Pm.Restore("work");

        Assert.True(File.Exists(Path.Combine(r.Paths.IsolatedDir("work"), "login.txt")));
        Assert.Empty(r.Pm.ListRemoved());
    }

    // ---------- SelectRemoved / Purge ----------

    [Fact]
    public void SelectRemoved_NeedsASelector()
    {
        using var r = new Rig();
        Assert.Throws<ClauderooException>(() => r.Pm.SelectRemoved(null));
        Assert.Throws<ClauderooException>(() => r.Pm.SelectRemoved("work", all: true));
        Assert.Throws<ClauderooException>(() => r.Pm.SelectRemoved(null, at: "20261101-090000", all: true));
    }

    [Fact]
    public void SelectRemoved_OlderThan_FiltersByRemovalTime()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        r.AddAndRemove("alpha", T0.AddDays(20));
        r.Now = T0.AddDays(31);

        var old = r.Pm.SelectRemoved(null, olderThan: TimeSpan.FromDays(30));

        Assert.Equal("work", Assert.Single(old).Name);
        Assert.Equal(2, r.Pm.SelectRemoved(null, all: true).Count);
        Assert.Empty(r.Pm.SelectRemoved("alpha", olderThan: TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Purge_JunctionIntoHome_HomeContentsSurvive()
    {
        using var r = new Rig();
        var cfg = r.Pm.Config;
        // A shared folder in home, with files.
        var skills = Path.Combine(cfg.HomeDir, "skills");
        Directory.CreateDirectory(Path.Combine(skills, "my-skill"));
        File.WriteAllText(Path.Combine(skills, "my-skill", "SKILL.md"), "keep me");
        r.AddAndRemove("work", T0);
        // Remove unlinks the junctions; put some back (as an older clauderoo or the user might have left them).
        var copy = r.Copy("work", T0);
        Junction.Create(Path.Combine(copy, "skills"), skills);
        Directory.CreateDirectory(Path.Combine(copy, "projects", "x"));
        Junction.Create(Path.Combine(copy, "projects", "x", "home-link"), cfg.HomeDir);
        Assert.True(File.Exists(Path.Combine(copy, "skills", "my-skill", "SKILL.md")));

        var result = r.Pm.Purge(r.Pm.SelectRemoved("work"));

        Assert.Empty(result.Failed);
        Assert.Single(result.Deleted);
        Assert.False(Directory.Exists(copy));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(skills, "my-skill", "SKILL.md")));
        Assert.True(File.Exists(cfg.HomeJson));
        Assert.True(Directory.Exists(cfg.HomeDir));
    }

    [Fact]
    public void Purge_DeletesIsolatedFolderToo_AndReportsFreedBytes()
    {
        using var r = new Rig();
        r.Pm.Add("work");
        var (iso, _) = r.Pm.PrepareIsolated("work");
        File.WriteAllBytes(Path.Combine(iso, "blob.bin"), new byte[4096]);
        r.Pm.Remove("work");
        var copy = Assert.Single(r.Pm.ListRemoved());

        var result = r.Pm.Purge([copy]);

        Assert.Equal(copy.Size, result.Freed);
        Assert.True(result.Freed >= 4096);
        Assert.Empty(Directory.GetDirectories(r.Paths.RemovedDir));
    }

    [Fact]
    public void Purge_OnlyDeletesWhatIsInRemoved()
    {
        using var r = new Rig();
        r.Pm.Add("work");
        var live = r.Paths.ProfileDir("work");
        File.WriteAllText(Path.Combine(live, "marker.txt"), "live");
        // A forged copy pointing at a live profile folder: Purge looks copies up again and ignores the paths given.
        var forged = new RemovedCopy("work", "20261101-090000", T0, 0, live, null);

        var result = r.Pm.Purge([forged]);

        Assert.Empty(result.Deleted);
        Assert.True(File.Exists(Path.Combine(live, "marker.txt")));
    }

    [Fact]
    public void Purge_LeavesOtherCopies()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        r.AddAndRemove("work", T0.AddDays(1));
        r.Pm.Purge([r.Pm.FindRemoved("work", "20261101-090000")]);
        Assert.Equal("20261102-090000", Assert.Single(r.Pm.ListRemoved()).Id);
    }

    // ---------- parsing and text ----------

    [Theory]
    [InlineData("30d", 30 * 24 * 60)]
    [InlineData("2w", 14 * 24 * 60)]
    [InlineData("12h", 12 * 60)]
    [InlineData("90m", 90)]
    [InlineData("7D", 7 * 24 * 60)]
    public void ParseAge_Units(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RemovedProfiles.ParseAge(text));

    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("d")]
    [InlineData("-3d")]
    [InlineData("3y")]
    public void ParseAge_Invalid_Refused(string text) =>
        Assert.Throws<ClauderooException>(() => RemovedProfiles.ParseAge(text));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(812, "812 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(12_900_000, "12.3 MB")]
    [InlineData(300L * 1024 * 1024, "300 MB")]
    public void FormatSize_ForPeople(long bytes, string text) => Assert.Equal(text, RemovedProfiles.FormatSize(bytes));

    [Fact]
    public void TrayConfirmText_ListsCopiesAndSize()
    {
        var c = new RemovedCopy("work", "20261101-090000", T0, 2048, "x", null);
        var text = RemovedText.ConfirmDelete([c]);
        Assert.Contains("1 removed copy (2 KB)", text);
        Assert.Contains("work", text);
        Assert.Contains("can't be undone", text);
    }

    // ---------- CLI ----------

    private static (int code, string o, string e) Run(Rig r, string? input, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, r.Pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd",
            hooks: new ReleaseHooks { ReadLine = () => input });
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Cli_Removed_ListsCopies()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code, o, _) = Run(r, null, "removed");
        Assert.Equal(0, code);
        Assert.Contains("work", o);
        Assert.Contains("2026-11-01 09:00", o);
        Assert.Contains("20261101-090000", o);
        Assert.Contains("1 copy", o);
    }

    [Fact]
    public void Cli_Removed_None()
    {
        using var r = new Rig();
        var (code, o, _) = Run(r, null, "removed");
        Assert.Equal(0, code);
        Assert.Contains("No removed profiles.", o);
    }

    [Fact]
    public void Cli_Removed_Json()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code, o, _) = Run(r, null, "removed", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(o);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var item = Assert.Single(root.GetProperty("removed").EnumerateArray().ToList());
        Assert.Equal("work", item.GetProperty("name").GetString());
        Assert.Equal("20261101-090000", item.GetProperty("id").GetString());
        Assert.EndsWith("Z", item.GetProperty("removedAt").GetString());
        Assert.True(item.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.True(item.GetProperty("hasProfile").GetBoolean());
        Assert.False(item.GetProperty("hasIsolated").GetBoolean());
        Assert.True(root.GetProperty("totalBytes").GetInt64() > 0);
    }

    [Fact]
    public void Cli_Purge_Declined_DeletesNothing()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code, o, _) = Run(r, "n", "purge", "--all");
        Assert.Equal(0, code);
        Assert.Contains("deleted permanently", o);
        Assert.Contains("Nothing was deleted.", o);
        Assert.True(Directory.Exists(r.Copy("work", T0)));
    }

    [Fact]
    public void Cli_Purge_NoInput_RefusesAndSuggestsYes()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code, _, e) = Run(r, null, "purge", "work");
        Assert.Equal(1, code);
        Assert.Contains("--yes", e);
        Assert.True(Directory.Exists(r.Copy("work", T0)));
    }

    [Fact]
    public void Cli_Purge_Confirmed_Deletes()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code, o, _) = Run(r, "y", "purge", "work");
        Assert.Equal(0, code);
        Assert.Contains("Deleted 1 copy, freed", o);
        Assert.False(Directory.Exists(r.Copy("work", T0)));
    }

    [Fact]
    public void Cli_Purge_OlderThan_Yes()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        r.AddAndRemove("alpha", T0.AddDays(25));
        r.Now = T0.AddDays(31);
        var (code, _, _) = Run(r, null, "purge", "--older-than", "30d", "--yes");
        Assert.Equal(0, code);
        Assert.False(Directory.Exists(r.Copy("work", T0)));
        Assert.True(Directory.Exists(r.Copy("alpha", T0.AddDays(25))));
    }

    [Fact]
    public void Cli_Purge_Json_NeedsYes_ThenPrintsResult()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0);
        var (code1, o1, e1) = Run(r, "y", "purge", "--all", "--json");
        Assert.Equal(1, code1);
        Assert.Equal("", o1);
        Assert.Contains("\"error\"", e1);
        Assert.True(Directory.Exists(r.Copy("work", T0)));

        var (code2, o2, _) = Run(r, null, "purge", "--all", "--json", "--yes");
        Assert.Equal(0, code2);
        using var doc = System.Text.Json.JsonDocument.Parse(o2);
        Assert.Equal("work", Assert.Single(doc.RootElement.GetProperty("deleted").EnumerateArray().ToList()).GetProperty("name").GetString());
        Assert.True(doc.RootElement.GetProperty("freedBytes").GetInt64() > 0);
    }

    [Fact]
    public void Cli_Purge_NoSelector_UsageError()
    {
        using var r = new Rig();
        var (code, _, e) = Run(r, "y", "purge");
        Assert.Equal(1, code);
        Assert.Contains("--all", e);
    }

    [Fact]
    public void Cli_Restore_At()
    {
        using var r = new Rig();
        r.AddAndRemove("work", T0, "old");
        r.AddAndRemove("work", T0.AddDays(1), "new");
        var (code, o, _) = Run(r, null, "restore", "work", "--at", "20261101-090000");
        Assert.Equal(0, code);
        Assert.Contains("Restored 'work'", o);
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Paths.ProfileDir("work"), "marker.txt")));
    }
}
