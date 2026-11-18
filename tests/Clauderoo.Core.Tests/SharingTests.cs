using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Clauderoo.Core.Tests;

// Per-item exclusions (#3) and extra shared items (#15). Every test also checks that home is never changed.
public class SharingTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0);

    private static (ProfileManager pm, TempHome h, string home, string work) Make()
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now);
        pm.Init();
        pm.Add("work");
        return (pm, h, h.Paths.DefaultHomeDir, h.Paths.ProfileDir("work"));
    }

    private static void Put(string file, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>Every file and folder under home (and .claude.json) with a content hash; links are listed, never followed.</summary>
    private static string Snapshot(TempHome h)
    {
        var lines = new List<string>();
        void Walk(string dir)
        {
            foreach (var d in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                var t = new DirectoryInfo(d).LinkTarget;
                lines.Add($"D {Path.GetRelativePath(h.Dir, d)} {t}");
                if (t == null) Walk(d);
            }
            foreach (var f in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
                lines.Add($"F {Path.GetRelativePath(h.Dir, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}");
        }
        Walk(h.Paths.DefaultHomeDir);
        lines.Add("J " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.Paths.DefaultHomeJson))));
        return string.Join("\n", lines);
    }

    private static void SeedSkills(string home)
    {
        Put(Path.Combine(home, "skills", "a", "SKILL.md"), "a");
        Put(Path.Combine(home, "skills", "b", "SKILL.md"), "b");
        Put(Path.Combine(home, "skills", "notes.md"), "notes");
    }

    // ---------- folders ----------

    [Fact]
    public void ExcludeSkill_MakesFilteredFolder_WithLinksForTheRest_HomeUntouched()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Sync("work");
        Assert.NotNull(Junction.GetTarget(Path.Combine(work, "skills"))); // whole-folder junction first
        var before = Snapshot(h);

        var r = pm.Exclude("work", "skills/a");

        Assert.Empty(r.Problems);
        var skills = Path.Combine(work, "skills");
        Assert.Null(Junction.GetTarget(skills));
        Assert.True(FilteredDir.IsFiltered(skills));
        Assert.False(Directory.Exists(Path.Combine(skills, "a")));
        Assert.Equal(Path.Combine(home, "skills", "b"), Junction.GetTarget(Path.Combine(skills, "b")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(skills, "notes.md")));
        Assert.Equal(["skills/a"], pm.Get("work").Exclude);
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void FilteredFolder_FollowsHome_NewEntriesAppear_RemovedOnesGo()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var skills = Path.Combine(work, "skills");

        Put(Path.Combine(home, "skills", "c", "SKILL.md"), "c");
        Put(Path.Combine(home, "skills", "new.md"), "new");
        Directory.Delete(Path.Combine(home, "skills", "b"), recursive: true); // the user removes b from home
        File.WriteAllText(Path.Combine(home, "skills", "notes.md"), "notes v2");
        var before = Snapshot(h);

        var r = pm.Sync("work");

        Assert.Empty(r.Problems);
        Assert.Equal(Path.Combine(home, "skills", "c"), Junction.GetTarget(Path.Combine(skills, "c")));
        Assert.False(Directory.Exists(Path.Combine(skills, "b")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(skills, "new.md")));
        Assert.Equal("notes v2", File.ReadAllText(Path.Combine(skills, "notes.md")));
        Assert.Empty(r.Notices); // the copy wasn't changed in the profile: nothing to back up
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void Include_LastExclusion_BackToOneJunction_HomeUntouched()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var before = Snapshot(h);

        var r = pm.Include("work", "skills/a");

        Assert.Empty(r.Problems);
        Assert.Equal(Path.Combine(home, "skills"), Junction.GetTarget(Path.Combine(work, "skills")));
        Assert.Empty(pm.Get("work").Exclude);
        Assert.Equal(before, Snapshot(h));
        Assert.Equal("b", File.ReadAllText(Path.Combine(home, "skills", "b", "SKILL.md")));
    }

    [Fact]
    public void Include_FolderWithOwnFiles_StaysFiltered_KeepsThem_Reported()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var mine = Path.Combine(work, "skills", "mine", "SKILL.md");
        Put(mine, "mine");
        var before = Snapshot(h);

        var r = pm.Include("work", "skills/a");

        Assert.Contains(r.Problems, p => p.Contains("holds files of its own"));
        Assert.Equal("mine", File.ReadAllText(mine));
        Assert.True(FilteredDir.IsFiltered(Path.Combine(work, "skills")));
        // With no exclusions left, a is shared again inside the filtered folder.
        Assert.Equal(Path.Combine(home, "skills", "a"), Junction.GetTarget(Path.Combine(work, "skills", "a")));
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void FilteredFolder_ChangedCopy_BackedUpWhenHomeChanges()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        Put(Path.Combine(home, "agents", "x.md"), "x");
        Put(Path.Combine(home, "agents", "y.md"), "y");
        pm.Exclude("work", "agents/x.md");
        var copy = Path.Combine(work, "agents", "y.md");
        Assert.Equal("y", File.ReadAllText(copy));
        Assert.False(File.Exists(Path.Combine(work, "agents", "x.md")));

        File.WriteAllText(copy, "edited in work");
        File.WriteAllText(Path.Combine(home, "agents", "y.md"), "y v2");
        var before = Snapshot(h);
        var r = pm.Sync("work");

        Assert.Equal("y v2", File.ReadAllText(copy));
        var backup = Path.Combine(work, "agents", "y.md.bak-20261118-090000");
        Assert.Equal("edited in work", File.ReadAllText(backup));
        Assert.Contains(r.Notices, n => n.Contains("y.md"));
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void ExcludeEntry_ProfileFolderIsRealFolder_NotTouched_Reported()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        Put(Path.Combine(home, "commands", "c.md"));
        Put(Path.Combine(home, "commands", "d.md"));
        var mine = Path.Combine(work, "commands", "mine.md");
        // A profile folder that is the profile's own (not a clauderoo folder) is never changed.
        Directory.CreateDirectory(work);
        Put(mine, "mine");

        var r = pm.Exclude("work", "commands/c.md");

        Assert.Contains(r.Problems, p => p.Contains("commands is a real folder"));
        Assert.Equal(["mine.md"], Directory.GetFiles(Path.Combine(work, "commands")).Select(Path.GetFileName));
    }

    [Fact]
    public void JunctionPointingElsewhere_NotConvertedOrDeleted()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        Put(Path.Combine(home, "hooks", "h.ps1"));
        var other = Path.Combine(h.Dir, "other");
        Put(Path.Combine(other, "keep.txt"), "keep");
        var link = Path.Combine(work, "hooks");
        Directory.CreateDirectory(work);
        Junction.Create(link, other);

        var r = pm.Exclude("work", "hooks/h.ps1");

        Assert.Contains(r.Problems, p => p.Contains("is linked to"));
        Assert.Equal(other, Junction.GetTarget(link));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(other, "keep.txt")));
    }

    [Fact]
    public void Own_FilteredFolder_BecomesRealCopy_NoLinksIntoHome()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var before = Snapshot(h);

        pm.Own("work", "skills");

        var skills = Path.Combine(work, "skills");
        Assert.Null(Junction.GetTarget(Path.Combine(skills, "b")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(skills, "b", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(skills, FilteredDir.MarkerName)));
        Assert.False(Directory.Exists(Path.Combine(skills, "a")));
        File.WriteAllText(Path.Combine(skills, "b", "SKILL.md"), "changed in work");
        Assert.Equal("b", File.ReadAllText(Path.Combine(home, "skills", "b", "SKILL.md")));
        pm.Sync("work"); // owned: left alone
        Assert.Equal("changed in work", File.ReadAllText(Path.Combine(skills, "b", "SKILL.md")));
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void Share_FilteredFolder_IsNotMovedAside()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");

        var r = pm.Share("work", "skills");

        Assert.Empty(r.Problems);
        Assert.Empty(Directory.GetDirectories(work, "skills.bak-*"));
        Assert.True(FilteredDir.IsFiltered(Path.Combine(work, "skills")));
    }

    [Fact]
    public void RemoveAndRestore_FilteredFolder_HomeUntouched_LinksComeBack()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var before = Snapshot(h);

        pm.Remove("work");
        Assert.Equal(before, Snapshot(h));
        var r = pm.Restore("work");

        Assert.DoesNotContain("skills", pm.Get("work").Overrides);
        Assert.Empty(r.Problems);
        Assert.Equal(Path.Combine(home, "skills", "b"), Junction.GetTarget(Path.Combine(work, "skills", "b")));
        Assert.False(Directory.Exists(Path.Combine(work, "skills", "a")));
        Assert.Equal(before, Snapshot(h));
    }

    // ---------- MCP servers ----------

    private static void SeedMcp(TempHome h) =>
        File.WriteAllText(h.Paths.DefaultHomeJson,
            """{"mcpServers":{"github":{"command":"gh"},"jira":{"command":"jira"}},"oauthAccount":{"emailAddress":"me@example.com"}}""");

    private static JsonObject ProfileMcp(string work) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(work, ".claude.json")))!["mcpServers"]!;

    [Fact]
    public void ExcludeMcpServer_CopiesTheOthers_IncludeBringsItBack_HomeUntouched()
    {
        var (pm, h, _, work) = Make();
        using var _h = h;
        SeedMcp(h);
        pm.Sync("work");
        Assert.Equal(2, ProfileMcp(work).Count);
        var before = Snapshot(h);

        var r = pm.Exclude("work", "mcpServers/jira");
        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices); // nothing to back up: the profile's copy was clauderoo's
        Assert.Equal(["github"], ProfileMcp(work).Select(kv => kv.Key));

        pm.Include("work", "mcpServers/jira");
        Assert.Equal(["github", "jira"], ProfileMcp(work).Select(kv => kv.Key));
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void ExcludeMcpServer_NamesAreExact()
    {
        var (pm, h, _, work) = Make();
        using var _h = h;
        SeedMcp(h);
        var r = pm.Exclude("work", "mcpServers/GitHub");
        Assert.Contains(r.Notices, n => n.Contains("isn't in"));
        Assert.Equal(2, ProfileMcp(work).Count);
    }

    // ---------- plugins ----------

    private static void SeedPlugins(string home)
    {
        Put(Path.Combine(home, "plugins", "installed_plugins.json"),
            """{"version":2,"plugins":{"foo@market":[{"scope":"user"}],"bar@market":[{"scope":"user"}]}}""");
        Put(Path.Combine(home, "settings.json"), """{"theme":"dark","enabledPlugins":{"foo@market":true,"bar@market":true}}""");
    }

    [Fact]
    public void ExcludePlugin_TurnsItOffInTheProfilesSettings_FolderStaysOneJunction()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        SeedPlugins(home);
        pm.Sync("work");
        var before = Snapshot(h);

        var r = pm.Exclude("work", "plugins/foo");

        Assert.Empty(r.Problems);
        Assert.Empty(r.Notices);
        Assert.Equal(Path.Combine(home, "plugins"), Junction.GetTarget(Path.Combine(work, "plugins")));
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "settings.json")))!;
        Assert.False(s["enabledPlugins"]!["foo@market"]!.GetValue<bool>());
        Assert.True(s["enabledPlugins"]!["bar@market"]!.GetValue<bool>());
        Assert.Equal("dark", (string?)s["theme"]);
        Assert.Equal(before, Snapshot(h));

        pm.Include("work", "plugins/foo");
        Assert.Equal(File.ReadAllText(Path.Combine(home, "settings.json")), File.ReadAllText(Path.Combine(work, "settings.json")));
        Assert.Empty(Directory.GetFiles(work, "settings.json.bak-*"));
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void ExcludePlugin_NotInstalled_Warns_TurnedOffOnceInstalled()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        Put(Path.Combine(home, "settings.json"), "{}");
        var r = pm.Exclude("work", "plugins/later@market");
        Assert.Contains(r.Notices, n => n.Contains("isn't in"));
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "settings.json")))!;
        Assert.False(s["enabledPlugins"]!["later@market"]!.GetValue<bool>());
        Assert.Equal("{}", File.ReadAllText(Path.Combine(home, "settings.json")));
    }

    [Fact]
    public void ExcludePlugin_ProfileOwnsSettings_RefusedWithAClearMessage()
    {
        var (pm, h, home, _) = Make();
        using var _h = h;
        SeedPlugins(home);
        pm.Own("work", "settings.json");
        var e = Assert.Throws<ClauderooException>(() => pm.Exclude("work", "plugins/foo"));
        Assert.Contains("owns settings.json", e.Message);
        Assert.Empty(pm.Get("work").Exclude);
    }

    // ---------- checks ----------

    [Theory]
    [InlineData("skills")]            // no entry
    [InlineData("skills/")]
    [InlineData("skills/../x")]
    [InlineData("skills/a\\b")]
    [InlineData("skills/..")]
    [InlineData("nope/x")]            // unknown item
    [InlineData("settings.json/x")]   // a file
    [InlineData("CLAUDE.md/x")]
    public void Exclude_BadSpec_Throws(string spec)
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.Exclude("work", spec));
        Assert.Empty(pm.Get("work").Exclude);
    }

    [Fact]
    public void Exclude_HomeProfile_Throws()
    {
        var (pm, h, home, _) = Make();
        using var _h = h;
        SeedSkills(home);
        Assert.Throws<ClauderooException>(() => pm.Exclude("personal", "skills/a"));
    }

    [Fact]
    public void Exclude_Missing_Allowed_WithWarning()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        SeedSkills(home);
        var r = pm.Exclude("work", "skills/ghost");
        Assert.Contains(r.Notices, n => n.Contains("ghost") && n.Contains("isn't in"));
        Assert.Equal(["skills/ghost"], pm.Get("work").Exclude);

        Put(Path.Combine(home, "skills", "ghost", "SKILL.md"));
        pm.Sync("work");
        Assert.False(Directory.Exists(Path.Combine(work, "skills", "ghost")));
    }

    [Fact]
    public void Include_NotExcluded_Throws()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.Include("work", "skills/a"));
    }

    [Fact]
    public void SharingOf_ShowsSharedOwnedAndExcluding()
    {
        var (pm, h, home, _) = Make();
        using var _h = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        pm.Own("work", "CLAUDE.md");

        var s = pm.SharingOf("work").ToDictionary(x => x.Item);

        Assert.Equal(ShareState.Excluding, s["skills"].State);
        Assert.Equal(["a"], s["skills"].Exclusions);
        Assert.Equal(ShareState.Owned, s["CLAUDE.md"].State);
        Assert.False(s["CLAUDE.md"].CanExclude);
        Assert.Equal(ShareState.Shared, s["mcpServers"].State);
        Assert.True(s["mcpServers"].CanExclude);
        Assert.Equal(["a", "b", "notes.md"], pm.HomeEntries("skills"));
    }

    [Fact]
    public void SetExclusions_ReplacesTheList()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        SeedSkills(home);
        pm.SetExclusions("work", "skills", ["a", "b"]);
        Assert.Equal(["skills/a", "skills/b"], pm.Get("work").Exclude);
        pm.SetExclusions("work", "skills", ["b"]);
        Assert.Equal(["skills/b"], pm.Get("work").Exclude);
        Assert.Equal(Path.Combine(home, "skills", "a"), Junction.GetTarget(Path.Combine(work, "skills", "a")));
        pm.SetExclusions("work", "skills", []);
        Assert.Equal(Path.Combine(home, "skills"), Junction.GetTarget(Path.Combine(work, "skills")));
    }

    // ---------- extra shared items ----------

    [Fact]
    public void AddShared_Folder_LinkedIntoProfiles_RemoveUnlinks_HomeUntouched()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        Put(Path.Combine(home, "rules", "r.md"), "rule");
        var before = Snapshot(h);

        var r = pm.AddShared("rules");
        Assert.Empty(r.Problems);
        Assert.Equal(["rules"], pm.Config.ExtraSharedDirs);
        Assert.Equal(Path.Combine(home, "rules"), Junction.GetTarget(Path.Combine(work, "rules")));
        Assert.Contains(pm.SharedList(), i => i.Name == "rules" && i.Kind == SharedKind.Folder && !i.BuiltIn && i.InHome);

        pm.RemoveShared("rules");
        Assert.False(Directory.Exists(Path.Combine(work, "rules")));
        Assert.Empty(pm.Config.ExtraSharedDirs);
        Assert.Equal(before, Snapshot(h));
    }

    [Fact]
    public void AddShared_File_CopiedOneWay_OwnAndExcludeWork()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        Put(Path.Combine(home, "statusline.ps1"), "echo hi");
        pm.AddShared("statusline.ps1");
        Assert.Equal("echo hi", File.ReadAllText(Path.Combine(work, "statusline.ps1")));
        Assert.Throws<ClauderooException>(() => pm.Exclude("work", "statusline.ps1/x"));

        pm.Own("work", "statusline.ps1");
        File.WriteAllText(Path.Combine(work, "statusline.ps1"), "mine");
        File.WriteAllText(Path.Combine(home, "statusline.ps1"), "echo v2");
        pm.Sync("work");
        Assert.Equal("mine", File.ReadAllText(Path.Combine(work, "statusline.ps1")));
    }

    [Fact]
    public void AddShared_ExtraFolder_SupportsExclusions()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        Put(Path.Combine(home, "rules", "a.md"), "a");
        Put(Path.Combine(home, "rules", "b.md"), "b");
        pm.AddShared("rules");
        pm.Exclude("work", "rules/a.md");
        Assert.False(File.Exists(Path.Combine(work, "rules", "a.md")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(work, "rules", "b.md")));

        pm.RemoveShared("rules");
        Assert.False(Directory.Exists(Path.Combine(work, "rules")));
        Assert.Empty(pm.Get("work").Exclude);
        Assert.Equal("a", File.ReadAllText(Path.Combine(home, "rules", "a.md")));
    }

    [Fact]
    public void AddShared_ProfileAlreadyHasOne_KeepsItAsItsOwn()
    {
        var (pm, h, home, work) = Make();
        using var _h = h;
        Put(Path.Combine(home, "rules", "a.md"), "home");
        Put(Path.Combine(work, "rules", "mine.md"), "mine");

        var r = pm.AddShared("rules");

        Assert.Contains("rules", pm.Get("work").Overrides);
        Assert.Contains(r.Notices, n => n.Contains("keeps its own rules"));
        Assert.Empty(r.Problems);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(work, "rules", "mine.md")));
    }

    [Fact]
    public void AddShared_Missing_NeedsKind()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.AddShared("later"));
        var r = pm.AddShared("later", SharedKind.Folder);
        Assert.Contains(r.Notices, n => n.Contains("isn't in"));
        Assert.Contains(pm.SharedList(), i => i.Name == "later" && !i.InHome);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData(".credentials.json")]
    [InlineData(".claude.json")]
    [InlineData("projects")]
    [InlineData("Projects")]
    [InlineData("history.jsonl")]
    [InlineData("sessions")]
    [InlineData("todos")]
    [InlineData("settings.json")]   // built in
    [InlineData("skills")]          // built in
    [InlineData(".clauderoo-sync.json")]
    [InlineData("my-credentials")]
    [InlineData("")]
    [InlineData(" x")]
    public void AddShared_BadName_Throws(string name)
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.AddShared(name, SharedKind.Folder));
        Assert.Empty(pm.Config.ExtraSharedDirs);
    }

    [Fact]
    public void RemoveShared_BuiltInOrUnknown_Throws()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.RemoveShared("skills"));
        Assert.Throws<ClauderooException>(() => pm.RemoveShared("settings.json"));
        Assert.Throws<ClauderooException>(() => pm.RemoveShared("mcpServers"));
        Assert.Throws<ClauderooException>(() => pm.RemoveShared("nope"));
    }

    [Fact]
    public void Export_Import_CarriesExclusions()
    {
        var (pm, h, home, _) = Make();
        using var _h = h;
        SeedSkills(home);
        pm.Exclude("work", "skills/a");
        var json = pm.Export(["work"], includeEnv: false);
        pm.Remove("work");

        pm.Import(json, force: false);

        Assert.Equal(["skills/a"], pm.Get("work").Exclude);
    }
}
