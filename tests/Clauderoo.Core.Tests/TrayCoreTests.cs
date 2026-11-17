using System.Diagnostics;

namespace Clauderoo.Core.Tests;

public class TrayCoreTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private static readonly DateTime T0 = new(2026, 11, 17, 9, 0, 0);

    private static (ProfileManager pm, TempHome h) Make()
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, h);
    }

    // ---- SessionTracker ----

    [Fact]
    public void Sessions_AliveSameStart_Running()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths, pid => (true, T0));
        t.Record("work", 42, T0);
        var r = Assert.Single(t.Running());
        Assert.Equal(new RunningSession("work", 42, T0), r);
        Assert.Single(t.Running("work"));
        Assert.Empty(t.Running("other"));
        Assert.True(File.Exists(Path.Combine(h.Paths.Root, "sessions.json")));
    }

    [Fact]
    public void Sessions_TryRunning_UnreadableFile_Null()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths, pid => (true, T0));
        t.Record("work", 42, T0);
        Assert.Single(t.TryRunning("work")!);
        using (new FileStream(Path.Combine(h.Paths.Root, "sessions.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(t.TryRunning("work")); // can't tell
            Assert.Empty(t.Running("work"));   // the CLI keeps its old behaviour
        }
        Assert.Single(t.TryRunning()!);
    }

    [Fact]
    public void Sessions_PidReused_NotRunning()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths, pid => (true, T0.AddHours(3))); // same pid, different process
        t.Record("work", 42, T0);
        Assert.Empty(t.Running());
        // and the stale entry was pruned from disk
        var again = new SessionTracker(h.Paths, pid => (true, T0));
        Assert.Empty(again.Running());
    }

    [Fact]
    public void Sessions_Dead_NotRunning_AndPruned()
    {
        using var h = new TempHome();
        var alive = true;
        var t = new SessionTracker(h.Paths, pid => (alive, alive ? T0 : null));
        t.Record("work", 42, T0);
        Assert.Single(t.Running());
        alive = false;
        Assert.Empty(t.Running());
    }

    [Fact]
    public void Sessions_CorruptFile_TreatedAsEmpty()
    {
        using var h = new TempHome();
        Directory.CreateDirectory(h.Paths.Root);
        File.WriteAllText(Path.Combine(h.Paths.Root, "sessions.json"), "{not json");
        var t = new SessionTracker(h.Paths, pid => (true, T0));
        Assert.Empty(t.Running());
        t.Record("work", 1, T0);
        Assert.Single(t.Running());
    }

    [Fact]
    public void Sessions_RecordRealProcess_RunsThenEnds()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add("pause");
        using var p = Process.Start(psi)!;
        try
        {
            t.Record("work", p);
            Assert.Single(t.Running("work"));
        }
        finally { p.Kill(); p.WaitForExit(); }
        Assert.Empty(t.Running("work"));
    }

    [Fact]
    public void Sessions_UnreadableFile_DoesNotWipeEntries()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths, pid => (true, T0));
        t.Record("work", 1, T0);
        var file = Path.Combine(h.Paths.Root, "sessions.json");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => t.Record("work", 2, T0));
            Assert.Empty(t.Running()); // can't read: reports nothing, changes nothing
        }
        Assert.Single(t.Running()); // entry 1 survived
    }

    [Fact]
    public void Sessions_ProbeThrows_UnknownNotRunningButKept()
    {
        using var h = new TempHome();
        var fail = false;
        var t = new SessionTracker(h.Paths, pid => fail ? throw new UnauthorizedAccessException() : (true, T0));
        t.Record("work", 1, T0);
        fail = true;
        Assert.Empty(t.Running());
        fail = false;
        Assert.Single(t.Running());
    }

    [Fact]
    public async Task Sessions_TwoTrackersInterleaved_NoEntriesLost()
    {
        using var h = new TempHome();
        Func<int, (bool, DateTime?)> probe = pid => (true, T0);
        var a = new SessionTracker(h.Paths, probe);
        var b = new SessionTracker(h.Paths, probe);
        var tasks = new[]
        {
            Task.Run(() => { for (var i = 0; i < 40; i++) { a.Record("a", 1000 + i, T0); a.Running(); } }),
            Task.Run(() => { for (var i = 0; i < 40; i++) { b.Record("b", 2000 + i, T0); b.Running("b"); } }),
        };
        await Task.WhenAll(tasks);
        Assert.Equal(40, a.Running("a").Count);
        Assert.Equal(40, b.Running("b").Count);
    }

    [Fact]
    public void Sessions_Rename_ReKeys()
    {
        using var h = new TempHome();
        var t = new SessionTracker(h.Paths, pid => (true, T0));
        t.Record("work", 1, T0);
        t.Rename("work", "job");
        Assert.Empty(t.Running("work"));
        Assert.Single(t.Running("job"));
    }
    // ---- Rename / Remove guard ----

    [Fact]
    public void Rename_RunningSession_Refused_ForceAllows()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Sessions = new SessionTracker(h.Paths, pid => (true, T0));
        pm.Sessions.Record("work", 7, T0);
        pm.Sessions.Record("work", 8, T0);
        var ex = Assert.Throws<SessionsRunningException>(() => pm.Rename("work", "job"));
        Assert.Equal("work has 2 running session(s) started by clauderoo. Close them first.", ex.Message);
        Assert.True(pm.Config.Profiles.ContainsKey("work"));

        pm.Rename("work", "job", force: true);
        Assert.True(pm.Config.Profiles.ContainsKey("job"));
        Assert.Equal(2, pm.Sessions.Running("job").Count);
        Assert.Empty(pm.Sessions.Running("work"));
    }

    [Fact]
    public void Remove_RunningSession_Refused_ForceAllows()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Sessions = new SessionTracker(h.Paths, pid => (true, T0));
        pm.Sessions.Record("work", 7, T0);
        var ex = Assert.Throws<SessionsRunningException>(() => pm.Remove("work"));
        Assert.Equal("work has 1 running session(s) started by clauderoo. Close them first.", ex.Message);
        Assert.True(Directory.Exists(h.Paths.ProfileDir("work")));

        pm.Remove("work", force: true);
        Assert.False(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void RenameRemove_NoRunningSession_Unaffected()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Sessions = new SessionTracker(h.Paths, pid => (false, null));
        pm.Sessions.Record("work", 7, T0);
        pm.Rename("work", "job");
        pm.Remove("job");
    }

    // ---- Restore ----

    [Fact]
    public void Restore_HappyPath_MovesBackAndReAdds()
    {
        var (pm, h) = Make(); using var _h = h;
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt"), "m");
        pm.Remove("work");
        Assert.False(Directory.Exists(h.Paths.ProfileDir("work")));

        var report = pm.Restore("work");

        Assert.Empty(report.Problems);
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt")));
        Assert.Empty(Directory.GetDirectories(h.Paths.RemovedDir));
    }

    [Fact]
    public void Restore_PicksNewest()
    {
        var (pm, h) = Make(); using var _h = h;
        var older = Path.Combine(h.Paths.RemovedDir, "work-20200101-000000");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "old.txt"), "o");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "new.txt"), "n");
        pm.Remove("work");

        pm.Restore("work");

        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "new.txt")));
        Assert.True(Directory.Exists(older)); // the older one is left alone
    }

    [Fact]
    public void Restore_NameExists_Refused()
    {
        var (pm, h) = Make(); using var _h = h;
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "marker.txt"), "m");
        pm.Remove("work");
        pm.Add("work"); // a new profile took the name
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("work"), "fresh.txt"), "f");

        var ex = Assert.Throws<ClauderooException>(() => pm.Restore("work"));

        Assert.Equal("A profile named 'work' already exists.", ex.Message);
        Assert.True(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), "fresh.txt")));
        Assert.Single(Directory.GetDirectories(h.Paths.RemovedDir)); // still there
    }

    [Fact]
    public void Restore_NothingRemoved_Refused()
    {
        var (pm, h) = Make(); using var _h = h;
        var ex = Assert.Throws<ClauderooException>(() => pm.Restore("ghost"));
        Assert.Equal("No removed profile named 'ghost' was found.", ex.Message);
    }

    [Fact]
    public void Restore_KeepsOwnedFolders_AsOverrides()
    {
        var (pm, h) = Make(); using var _h = h;
        Directory.CreateDirectory(Path.Combine(h.Paths.DefaultHomeDir, "plugins"));
        pm.Sync("work");
        pm.Own("work", "plugins");
        pm.Remove("work");

        var report = pm.Restore("work");

        Assert.Contains("plugins", pm.Config.Profiles["work"].Overrides);
        Assert.Empty(report.Problems);
    }

    // ---- API-key check wording ----

    [Fact]
    public void ApiKeyCheck_SavedButUnverified()
    {
        var h = new TempHome(); using var _h = h;
        var secrets = new FakeSecretStore();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, secrets, new FakeGcloud());
        pm.Init(); pm.Add("work");
        pm.SetProvider("work", Providers.ApiKey, null, "sk-test");
        var r = pm.Check("work");
        Assert.True(r.Ok);
        Assert.False(r.Verified);
        Assert.Equal("Key saved — not verified. Start a session to test it.", r.Message);
    }

    [Fact]
    public void OtherChecks_AreVerified()
    {
        var (pm, h) = Make(); using var _h = h;
        Assert.True(pm.Check("work").Verified); // login: a plain fact about the file
        Assert.True(new CheckResult(true, "x").Verified);
    }

    // ---- Problems ----

    [Fact]
    public void Translator_RealFolder_OffersShareAgain()
    {
        var p = ProblemTranslator.Translate("work: plugins is a real folder, not shared (run 'clauderoo share work plugins' to share it)");
        Assert.Equal("work", p.Profile);
        Assert.Equal(ProblemFix.ShareAgain, p.Fix);
        Assert.DoesNotContain("clauderoo", p.Title);
        Assert.Contains("plugins", p.Title);
        Assert.Contains("real folder", p.Detail);
    }

    [Fact]
    public void Translator_WrongLink_OffersRecreateLink()
    {
        var p = ProblemTranslator.Translate(@"work: skills is linked to C:\a, not to C:\b");
        Assert.Equal(ProblemFix.RecreateLink, p.Fix);
        Assert.Equal("work", p.Profile);
        Assert.Contains("skills", p.Title);
    }

    [Fact]
    public void Translator_FileInTheWay_NoFix()
    {
        var p = ProblemTranslator.Translate("work: plugins is a file, not a shared folder");
        Assert.Null(p.Fix);
        Assert.Contains("plugins", p.Title);
    }

    [Theory]
    [InlineData("work: could not read sync state: Access to the path 'x' is denied.")]
    [InlineData("work: could not sync mcpServers: boom")]
    [InlineData("work: could not save sync state: disk full")]
    [InlineData("work: could not sync: boom")]
    [InlineData("work: could not sync plugins: Access denied")]
    [InlineData("work: could not sync CLAUDE.md: locked")]
    [InlineData("work: could not save the model Claude chose: locked")]
    [InlineData("work: settings.json kept changing; provider settings not applied")]
    [InlineData("work: .claude.json kept changing; MCP servers not synced")]
    public void Translator_TransientFailures_OfferResync(string raw)
    {
        var p = ProblemTranslator.Translate(raw);
        Assert.Equal("work", p.Profile);
        Assert.Equal(ProblemFix.Resync, p.Fix);
        Assert.Equal(raw, p.Detail);
        Assert.DoesNotContain("could not", p.Title);
        Assert.DoesNotContain("Access", p.Title);
        Assert.DoesNotContain("clauderoo", p.Title);
    }

    [Theory]
    [InlineData("work: home settings.json is not valid JSON; settings not synced")]
    [InlineData("work: home settings.json \"env\" is not an object; settings not synced")]
    [InlineData("work: settings.json is not valid JSON; provider settings not applied")]
    [InlineData("work: settings.json \"env\" is not an object; provider settings not applied")]
    [InlineData("work: home .claude.json is not valid JSON; MCP servers not synced")]
    [InlineData("work: .claude.json is not valid JSON; MCP servers not synced")]
    public void Translator_BrokenFiles_PlainTitle_NoFix(string raw)
    {
        var p = ProblemTranslator.Translate(raw);
        Assert.Equal("work", p.Profile);
        Assert.Null(p.Fix);
        Assert.Equal(raw, p.Detail);
        Assert.DoesNotContain("JSON", p.Title);
        Assert.DoesNotContain("\"env\"", p.Title);
    }

    [Fact]
    public void Translator_UnknownProblem_KeepsText()
    {
        var raw = "work: the flux capacitor overheated (0x80070005)";
        var p = ProblemTranslator.Translate(raw);
        Assert.Equal("Something needs attention in work", p.Title);
        Assert.Equal(raw, p.Detail);
        Assert.Null(p.Fix);
        Assert.Equal("work", p.Profile);

        var noPrefix = ProblemTranslator.Translate("totally odd");
        Assert.Equal("totally odd", noPrefix.Detail);
        Assert.StartsWith("Something needs attention", noPrefix.Title);
    }

    [Fact]
    public void Translator_EveryProblemKeepsOriginalInDetail()
    {
        foreach (var raw in new[]
        {
            "a: plugins is a real folder, not shared (run 'clauderoo share a plugins' to share it)",
            @"a: skills is linked to C:\x, not to C:\y", "a: could not sync: z", "a: whatever",
        })
            Assert.Contains(raw, ProblemTranslator.Translate(raw).Detail);
    }

    [Fact]
    public void SyncReport_ProblemItems_MirrorsProblems()
    {
        var r = new SyncReport();
        r.Problems.Add("work: could not sync: x");
        r.Problems.Add("work: odd");
        Assert.Equal(2, r.ProblemItems.Count);
        Assert.Equal(ProblemFix.Resync, r.ProblemItems[0].Fix);
        Assert.Equal("work: odd", r.ProblemItems[1].Detail);
    }

    [Fact]
    public void Fix_ShareAgain_SharesTheFolder()
    {
        var (pm, h) = Make(); using var _h = h;
        Directory.CreateDirectory(Path.Combine(h.Paths.DefaultHomeDir, "plugins"));
        pm.Sync("work");
        pm.Own("work", "plugins"); // now a real copy
        var cfg = pm.Config; // config says shared, disk says real folder
        cfg.Profiles["work"].Overrides.Remove("plugins");
        ConfigStore.Save(h.Paths, cfg);

        var problem = Assert.Single(pm.SyncAll().ProblemItems);
        Assert.Equal(ProblemFix.ShareAgain, problem.Fix);

        var after = pm.Fix(problem);

        Assert.Empty(after.Problems);
        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("work"), "plugins")));
    }

    [Fact]
    public void Fix_RecreateLink_RepointsJunction()
    {
        var (pm, h) = Make(); using var _h = h;
        var home = Path.Combine(h.Paths.DefaultHomeDir, "plugins");
        Directory.CreateDirectory(home);
        pm.Sync("work");
        var link = Path.Combine(h.Paths.ProfileDir("work"), "plugins");
        var elsewhere = Path.Combine(h.Dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(link);
        Junction.Create(link, elsewhere);

        var problem = Assert.Single(pm.SyncAll().ProblemItems);
        Assert.Equal(ProblemFix.RecreateLink, problem.Fix);

        var after = pm.Fix(problem);

        Assert.Empty(after.Problems);
        Assert.True(Directory.Exists(elsewhere)); // never deletes the target
        Assert.Equal(Path.TrimEndingDirectorySeparator(home),
            Path.TrimEndingDirectorySeparator(Junction.GetTarget(link)!), ignoreCase: true);
    }

    [Fact]
    public void Fix_Resync_RunsSyncForProfile()
    {
        var (pm, h) = Make(); using var _h = h;
        var after = pm.Fix(new Problem("work", "t", "work: could not sync: x", ProblemFix.Resync));
        Assert.Empty(after.Problems);
    }

    [Fact]
    public void Fix_NoFix_Throws()
    {
        var (pm, h) = Make(); using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t", "d", null)));
    }

    [Fact]
    public void Fix_ValidatesItemAndProfile()
    {
        var (pm, h) = Make(); using var _h = h;
        // item not a shared folder
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t", "work: bogus is linked to a, not to b", ProblemFix.RecreateLink)));
        // a shared file is not a folder
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t", "work: settings.json is linked to a, not to b", ProblemFix.RecreateLink)));
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t", "work: bogus is a real folder, not shared", ProblemFix.ShareAgain)));
        // Detail names another profile
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t", "other: plugins is a real folder, not shared", ProblemFix.ShareAgain)));
    }

    [Fact]
    public void Restore_RecreatesSharedFolderJunction()
    {
        var (pm, h) = Make(); using var _h = h;
        Directory.CreateDirectory(Path.Combine(h.Paths.DefaultHomeDir, "plugins"));
        pm.Sync("work");
        pm.Remove("work");
        pm.Restore("work");
        Assert.NotNull(Junction.GetTarget(Path.Combine(h.Paths.ProfileDir("work"), "plugins")));
    }

    [Fact]
    public void Restore_KeepsVertexAndEnv()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.SetProvider("work", Providers.Vertex, new VertexSettings { Project = "p1", Region = "r1", SonnetModel = "s" });
        pm.SetEnv("work", [new("FOO", "bar")], []);
        pm.Remove("work");
        var report = pm.Restore("work");
        var e = pm.Config.Profiles["work"];
        Assert.Equal(Providers.Vertex, e.Provider);
        Assert.Equal("p1", e.Vertex!.Project);
        Assert.Equal("bar", e.Env["FOO"]);
        Assert.DoesNotContain(report.Notices, n => n.Contains("API key"));
        Assert.False(File.Exists(Path.Combine(h.Paths.ProfileDir("work"), ".clauderoo-entry.json")));
    }

    [Fact]
    public void Restore_ApiKeyProfile_BackAsApiKeyWithNotice_NoKeyOnDisk()
    {
        var h = new TempHome(); using var _h = h;
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now, new DpapiSecretStore(), new FakeGcloud())
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init(); pm.Add("work");
        pm.SetProvider("work", Providers.ApiKey, null, "sk-secret-123");
        pm.Remove("work");
        var saved = Directory.GetFiles(h.Paths.RemovedDir, ".clauderoo-entry.json", SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("sk-secret-123", File.ReadAllText(saved));

        var report = pm.Restore("work");

        Assert.Equal(Providers.ApiKey, pm.Config.Profiles["work"].Provider);
        Assert.False(pm.HasApiKey("work"));
        Assert.Contains("work is back. Its API key was deleted when it was removed — enter it again in Manage profiles.", report.Notices);
    }
    // ---- CLI ----

    [Fact]
    public void Cli_Restore_RestoresProfile()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Remove("work");
        var o = new StringWriter(); var e = new StringWriter();
        var code = Clauderoo.Cli.Commands.Run(["restore", "work"], o, e, pm, _ => 0);
        Assert.Equal(0, code);
        Assert.Contains("Restored 'work'", o.ToString());
        Assert.True(pm.Config.Profiles.ContainsKey("work"));
    }

    [Fact]
    public void Cli_Remove_RunningSession_NeedsForce()
    {
        var (pm, h) = Make(); using var _h = h;
        pm.Sessions = new SessionTracker(h.Paths, pid => (true, T0));
        pm.Sessions.Record("work", 7, T0);
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(1, Clauderoo.Cli.Commands.Run(["remove", "work"], o, e, pm, _ => 0));
        Assert.Contains("running session(s)", e.ToString());
        Assert.Contains("(use --force to do it anyway)", e.ToString());
        Assert.Equal(0, Clauderoo.Cli.Commands.Run(["remove", "work", "--force"], o, e, pm, _ => 0));
    }

    [Fact]
    public void Launcher_Specs_CarryProfile()
    {
        var (pm, h) = Make(); using var _h = h;
        var spec = Launcher.NewWindow(pm.Config, h.Paths, "work", h.Dir, [], false, @"C:\x\clauderoo.exe");
        Assert.Null(spec.Profile); // the in-terminal `run` records the session, not the terminal host
        Assert.Equal("work", Launcher.InPlace(pm.Config, h.Paths, "work", h.Dir, [], "claude.exe").Profile);
    }
}
