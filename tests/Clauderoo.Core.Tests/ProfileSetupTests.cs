using System.Text.Json.Nodes;
using Clauderoo.Cli;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

public class ProfileSetupTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);
    private const string Key = "sk-ant-api03-FAKE-KEY-123";
    private const string Token = "sk-ant-oat01-FAKE-TOKEN-456";

    private static (ProfileManager pm, TempHome home, FakeSecretStore keys, FakeSecretStore tokens) Make()
    {
        var home = new TempHome();
        // Shared folders in home, so profiles get junctions to them.
        Directory.CreateDirectory(Path.Combine(home.Paths.DefaultHomeDir, "skills"));
        Directory.CreateDirectory(Path.Combine(home.Paths.DefaultHomeDir, "plugins"));
        File.WriteAllText(Path.Combine(home.Paths.DefaultHomeDir, "settings.json"), "{}");
        var keys = new FakeSecretStore();
        var tokens = new FakeSecretStore();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, keys, new FakeGcloud(), tokens)
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        return (pm, home, keys, tokens);
    }

    private static (int code, string o, string e) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Commands.Run(args, o, e, pm, _ => 0, _ => 0, wtAvailable: true, claudePath: "claude.cmd");
        return (code, o.ToString(), e.ToString());
    }

    private static string NewDir(TempHome h, string name)
    {
        var d = Path.Combine(h.Dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------- start folder ----------

    [Fact]
    public void SetStartDir_StoresFullPath_AndClears()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var dir = NewDir(h, "code");
        pm.SetStartDir("work", dir + "\\");
        Assert.Equal(dir, pm.Get("work").StartDir);
        Assert.Equal(dir, pm.List().Single(i => i.Name == "work").StartDir);
        pm.SetStartDir("work", null);
        Assert.Null(pm.Get("work").StartDir);
    }

    [Fact]
    public void SetStartDir_MissingFolder_Refused_ConfigUnchanged()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var dir = NewDir(h, "code");
        pm.SetStartDir("work", dir);
        var e = Assert.Throws<ClauderooException>(() => pm.SetStartDir("work", Path.Combine(h.Dir, "nope")));
        Assert.Contains("Folder not found", e.Message);
        Assert.Equal(dir, pm.Get("work").StartDir);
    }

    [Fact]
    public void SetStartDir_HomeProfile_Allowed_HomeFolderUntouched()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        var before = Directory.GetFileSystemEntries(h.Paths.DefaultHomeDir).Order().ToList();
        var dir = NewDir(h, "code");
        pm.SetStartDir("personal", dir);
        Assert.Equal(dir, pm.Get("personal").StartDir);
        Assert.Equal(before, Directory.GetFileSystemEntries(h.Paths.DefaultHomeDir).Order().ToList());
    }

    [Fact]
    public void Cli_StartDir_ShowSetClear()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var dir = NewDir(h, "code");
        Assert.Contains("work has no start folder", Run(pm, "startdir", "work").o);
        var set = Run(pm, "startdir", "work", dir);
        Assert.Equal(0, set.code);
        Assert.Contains($"work now starts in {dir}.", set.o);
        Assert.Contains($"work starts in {dir}.", Run(pm, "startdir", "work").o);
        var clear = Run(pm, "startdir", "work", "--clear");
        Assert.Equal(0, clear.code);
        Assert.Null(pm.Get("work").StartDir);
    }

    [Fact]
    public void Cli_StartDir_Errors()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var missing = Run(pm, "startdir", "work", Path.Combine(h.Dir, "nope"));
        Assert.Equal(1, missing.code);
        Assert.Contains("Folder not found", missing.e);
        var unknown = Run(pm, "startdir", "ghost", h.Dir);
        Assert.Equal(1, unknown.code);
        Assert.Contains("Unknown profile 'ghost'", unknown.e);
        Assert.Contains("Usage: clauderoo startdir", Run(pm, "startdir").e);
        Assert.Contains("Usage: clauderoo startdir", Run(pm, "startdir", "work", "--bogus").e);
    }

    [Fact]
    public void Cli_List_ShowsStartDir_TextAndJson()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var dir = NewDir(h, "code");
        pm.SetStartDir("work", dir);
        var text = Run(pm, "list").o;
        Assert.Contains($"starts in {dir}", text.Split('\n').Single(l => l.Contains("work")));
        var json = JsonNode.Parse(Run(pm, "list", "--json").o)!;
        var work = json["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "work")!;
        Assert.Equal(dir, work["startDir"]!.GetValue<string>());
        var personal = json["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "personal")!;
        Assert.Null(personal["startDir"]);
    }

    // ---------- clone ----------

    [Fact]
    public void Clone_Vertex_CopiesSettings_NotLoginOrHistory()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("gcp");
        pm.SetProvider("gcp", Providers.Vertex, new VertexSettings { Project = "proj-1", Region = "us-east5", OpusModel = "opus-x" });
        pm.SetEnv("gcp", [new("MY_VAR", "v1")], []);
        pm.Own("gcp", "skills");
        File.WriteAllText(Path.Combine(h.Paths.ProfileDir("gcp"), "skills", "mine.md"), "own skill");
        var dir = NewDir(h, "code");
        pm.SetStartDir("gcp", dir);
        pm.Map("gcp", dir);
        // Things that belong to the source's sign-in and history.
        var src = h.Paths.ProfileDir("gcp");
        File.WriteAllText(Path.Combine(src, ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"me@x.com\"}}");
        File.WriteAllText(Path.Combine(src, ".credentials.json"), "{\"secret\":1}");
        Directory.CreateDirectory(Path.Combine(src, "projects", "p"));
        File.WriteAllText(Path.Combine(src, "projects", "p", "c.jsonl"), "{}");

        var r = pm.Clone("gcp", "gcp2");

        Assert.Null(r.SecretToSet);
        var e = pm.Get("gcp2");
        Assert.Equal(Providers.Vertex, e.Provider);
        Assert.Equal("proj-1", e.Vertex!.Project);
        Assert.Equal("us-east5", e.Vertex.Region);
        Assert.Equal("opus-x", e.Vertex.OpusModel);
        Assert.Equal("v1", e.Env["MY_VAR"]);
        Assert.Equal(["skills"], e.Overrides);
        Assert.Equal(dir, e.StartDir);
        Assert.False(e.Home);
        // Separate objects: editing the copy never changes the source.
        Assert.NotSame(pm.Config.Profiles["gcp"].Env, pm.Config.Profiles["gcp2"].Env);

        var dst = h.Paths.ProfileDir("gcp2");
        Assert.False(File.Exists(Path.Combine(dst, ".credentials.json")));
        Assert.False(Directory.Exists(Path.Combine(dst, "projects")));
        Assert.DoesNotContain("me@x.com", File.Exists(Path.Combine(dst, ".claude.json")) ? File.ReadAllText(Path.Combine(dst, ".claude.json")) : "");
        // The owned folder is a real copy of the source's own version, not a link; shared folders are linked to home.
        Assert.Null(Junction.GetTarget(Path.Combine(dst, "skills")));
        Assert.Equal("own skill", File.ReadAllText(Path.Combine(dst, "skills", "mine.md")));
        File.WriteAllText(Path.Combine(dst, "skills", "mine.md"), "changed in copy");
        Assert.Equal("own skill", File.ReadAllText(Path.Combine(src, "skills", "mine.md")));
        Assert.NotNull(Junction.GetTarget(Path.Combine(dst, "plugins")));
        // Folder mappings stay with the source.
        Assert.Equal("gcp", pm.Config.Folders.Single().Value);
    }

    [Fact]
    public void Clone_OwnedFileAndMcpServers_CopiesOnlyThoseNotSignIn()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("a");
        pm.Own("a", "CLAUDE.md");
        pm.Own("a", SharedItems.McpServers);
        var src = h.Paths.ProfileDir("a");
        File.WriteAllText(Path.Combine(src, "CLAUDE.md"), "a's own notes");
        File.WriteAllText(Path.Combine(src, ".claude.json"),
            "{\"oauthAccount\":{\"emailAddress\":\"me@x.com\"},\"mcpServers\":{\"mine\":{\"command\":\"x\"}}}");

        pm.Clone("a", "b");

        var dst = h.Paths.ProfileDir("b");
        Assert.Equal("a's own notes", File.ReadAllText(Path.Combine(dst, "CLAUDE.md")));
        var json = File.ReadAllText(Path.Combine(dst, ".claude.json"));
        Assert.Contains("\"mine\"", json);
        Assert.DoesNotContain("me@x.com", json);
        Assert.DoesNotContain("oauthAccount", json);
    }

    [Theory]
    [InlineData(Providers.ApiKey)]
    [InlineData(Providers.Token)]
    public void Clone_SecretProfile_StartsOnLogin_SecretNotCopied(string provider)
    {
        var (pm, h, keys, tokens) = Make(); using var _h = h;
        pm.Add("src");
        if (provider == Providers.ApiKey) pm.SetProvider("src", Providers.ApiKey, apiKey: Key);
        else pm.SetProvider("src", Providers.Token, token: Token);

        var r = pm.Clone("src", "copy");

        Assert.Equal(provider, r.SecretToSet);
        Assert.Equal(Providers.Login, Providers.Of(pm.Get("copy")));
        var dst = h.Paths.ProfileDir("copy");
        Assert.False(keys.Exists(dst));
        Assert.False(tokens.Exists(dst));
        // The source keeps its secret.
        Assert.True(provider == Providers.ApiKey ? keys.Exists(h.Paths.ProfileDir("src")) : tokens.Exists(h.Paths.ProfileDir("src")));
        // No secret leaks into the copy's files.
        foreach (var f in Directory.GetFiles(dst, "*", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(Key, text);
            Assert.DoesNotContain(Token, text);
        }
    }

    [Fact]
    public void Clone_ExistingName_OrFolder_Refused()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("a");
        pm.Add("b");
        Assert.Contains("already exists", Assert.Throws<ClauderooException>(() => pm.Clone("a", "b")).Message);
        Directory.CreateDirectory(h.Paths.ProfileDir("stray"));
        Assert.Contains("already exists", Assert.Throws<ClauderooException>(() => pm.Clone("a", "stray")).Message);
        Directory.CreateDirectory(h.Paths.IsolatedDir("iso"));
        Assert.Contains("isolated folder", Assert.Throws<ClauderooException>(() => pm.Clone("a", "iso")).Message);
        Assert.Throws<ClauderooException>(() => pm.Clone("ghost", "c"));
        Assert.Throws<ClauderooException>(() => pm.Clone("a", "Bad Name"));
        Assert.False(pm.Config.Profiles.ContainsKey("stray"));
        Assert.False(pm.Config.Profiles.ContainsKey("iso"));
    }

    [Fact]
    public void Clone_Home_CopiesOnlyStartDir_NotHomeContents()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        var homeDir = h.Paths.DefaultHomeDir;
        File.WriteAllText(Path.Combine(homeDir, ".credentials.json"), "{\"secret\":1}");
        Directory.CreateDirectory(Path.Combine(homeDir, "projects", "p"));
        var dir = NewDir(h, "code");
        pm.SetStartDir("personal", dir);

        var r = pm.Clone("personal", "fresh");

        Assert.Null(r.SecretToSet);
        var e = pm.Get("fresh");
        Assert.False(e.Home);
        Assert.Equal(Providers.Login, Providers.Of(e));
        Assert.Empty(e.Overrides);
        Assert.Equal(dir, e.StartDir);
        var dst = h.Paths.ProfileDir("fresh");
        Assert.False(File.Exists(Path.Combine(dst, ".credentials.json")));
        Assert.False(Directory.Exists(Path.Combine(dst, "projects")));
        Assert.Equal("personal", pm.Config.HomeProfileName);
    }

    [Fact]
    public void Cli_Clone_And_AddFrom()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("src");
        pm.SetProvider("src", Providers.ApiKey, apiKey: Key);
        pm.SetEnv("src", [new("A", "1")], []);

        var c = Run(pm, "clone", "src", "c1");
        Assert.Equal(0, c.code);
        Assert.Contains("Created 'c1' from 'src'", c.o);
        Assert.Contains("Not copied: the sign-in, conversations, API key or token", c.o);
        Assert.Contains("'cr provider c1 apikey'", c.o);
        Assert.DoesNotContain(Key, c.o + c.e);
        Assert.Equal("1", pm.Get("c1").Env["A"]);

        var a = Run(pm, "add", "c2", "--from", "src");
        Assert.Equal(0, a.code);
        Assert.Contains("Created 'c2' from 'src'", a.o);
        Assert.Equal("1", pm.Get("c2").Env["A"]);

        Assert.Contains("Usage: clauderoo clone <source> <new>", Run(pm, "clone", "src").e);
        Assert.Contains("Usage: clauderoo add <name> [--from <profile>]", Run(pm, "add", "c3", "--from").e);
        var dup = Run(pm, "clone", "src", "c1");
        Assert.Equal(1, dup.code);
        Assert.Contains("already exists", dup.e);
    }

    [Fact]
    public void Cli_Clone_Login_TellsToSignIn()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var r = Run(pm, "clone", "work", "work2");
        Assert.Equal(0, r.code);
        Assert.Contains("Run 'clauderoo start work2' and sign in.", r.o);
    }

    [Fact]
    public void TrayText_Duplicated_SaysWhatWasntCopied()
    {
        Assert.Contains("sign-in, conversations", TrayText.Duplicated("a", "b", null));
        Assert.Contains("choose API key", TrayText.Duplicated("a", "b", Providers.ApiKey));
        Assert.Contains("choose Claude token", TrayText.Duplicated("a", "b", Providers.Token));
    }

    // ---------- home ----------

    private static string MovedHome(TempHome h)
    {
        var moved = NewDir(h, "claude-moved");
        File.WriteAllText(Path.Combine(moved, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(moved, "skills"));
        Directory.CreateDirectory(Path.Combine(moved, "plugins"));
        return moved;
    }

    [Fact]
    public void SetHome_RelinksSharedFolders_AndUpdatesConfig()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var link = Path.Combine(h.Paths.ProfileDir("work"), "skills");
        Assert.Equal(Path.Combine(h.Paths.DefaultHomeDir, "skills"), Junction.GetTarget(link)!.TrimEnd('\\'));
        var moved = MovedHome(h);

        var r = pm.SetHome(moved);

        Assert.Empty(r.Report.Problems);
        var cfg = pm.Config;
        Assert.Equal(moved, cfg.HomeDir);
        Assert.Equal(Path.Combine(moved, ".claude.json"), cfg.HomeJson);
        Assert.False(r.HomeJsonExists);
        Assert.Equal(Path.Combine(moved, "skills"), Junction.GetTarget(link)!.TrimEnd('\\'));
        // The old home's folders are untouched.
        Assert.True(Directory.Exists(Path.Combine(h.Paths.DefaultHomeDir, "skills")));
    }

    [Fact]
    public void SetHome_NeverWritesIntoNewHome()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var moved = MovedHome(h);
        var before = Directory.GetFileSystemEntries(moved, "*", SearchOption.AllDirectories)
            .Select(p => (p, File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default)).Order().ToList();
        pm.SetHome(moved);
        var after = Directory.GetFileSystemEntries(moved, "*", SearchOption.AllDirectories)
            .Select(p => (p, File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default)).Order().ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void SetHome_DefaultFolder_UsesDefaultClaudeJson()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.SetHome(MovedHome(h));
        var r = pm.SetHome(h.Paths.DefaultHomeDir);
        Assert.Equal(h.Paths.DefaultHomeJson, pm.Config.HomeJson);
        Assert.True(r.HomeJsonExists);
    }

    [Fact]
    public void SetHome_Validates()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        var original = pm.Config.HomeDir;
        Assert.Contains("Folder not found", Assert.Throws<ClauderooException>(() => pm.SetHome(Path.Combine(h.Dir, "nope"))).Message);
        var empty = NewDir(h, "empty");
        Assert.Contains("doesn't look like a Claude config folder", Assert.Throws<ClauderooException>(() => pm.SetHome(empty)).Message);
        pm.Add("work");
        Assert.Contains("clauderoo's own folders", Assert.Throws<ClauderooException>(() => pm.SetHome(h.Paths.ProfileDir("work"))).Message);
        Assert.Contains("clauderoo's own folders", Assert.Throws<ClauderooException>(() => pm.SetHome(h.Paths.Root)).Message);
        Assert.Equal(original, pm.Config.HomeDir);
    }

    [Fact]
    public void Cli_Home_ShowAndSet()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var show = Run(pm, "home");
        Assert.Equal(0, show.code);
        Assert.Contains($"Home folder: {h.Paths.DefaultHomeDir}", show.o);
        Assert.DoesNotContain("(missing)", show.o.Split('\n')[0]);

        var moved = MovedHome(h);
        var set = Run(pm, "home", moved);
        Assert.Equal(0, set.code);
        Assert.Contains($"The home profile (personal) now uses {moved}.", set.o);
        Assert.Contains("doesn't exist yet", set.e);

        var bad = Run(pm, "home", NewDir(h, "empty"));
        Assert.Equal(1, bad.code);
        Assert.Contains("doesn't look like a Claude config folder", bad.e);
    }

    // ---------- missing home ----------

    private static void HideHome(TempHome h) =>
        Directory.Move(h.Paths.DefaultHomeDir, Path.Combine(h.Dir, ".claude-gone"));

    [Fact]
    public void MissingHome_ReportedAsProblem_InSyncAndStatus()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        HideHome(h);

        var sync = pm.SyncAll();
        var raw = Assert.Single(sync.Problems);
        Assert.StartsWith("work: the home Claude folder is missing:", raw);
        Assert.Contains("clauderoo home <path>", raw);
        var p = Assert.Single(sync.ProblemItems);
        Assert.Equal("work", p.Profile);
        Assert.Contains("can't reach the home Claude folder", p.Title);
        Assert.Null(p.Fix);
        Assert.True(TrayText.IsSevere(p));
        Assert.Contains("cr home", TrayText.Explain(p));
        Assert.DoesNotContain("run '", TrayText.Explain(p));

        Assert.Contains(pm.Status().Problems, x => x.Contains("home Claude folder is missing"));
        var status = Run(pm, "status");
        Assert.Contains("warning: work: the home Claude folder is missing", status.e);
    }

    [Fact]
    public void MissingHome_ThenSetHome_ClearsProblem()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        HideHome(h);
        Assert.NotEmpty(pm.SyncAll().Problems);
        var r = pm.SetHome(Path.Combine(h.Dir, ".claude-gone"));
        Assert.Empty(r.Report.Problems);
        Assert.Empty(pm.SyncAll().Problems);
        var link = Path.Combine(h.Paths.ProfileDir("work"), "skills");
        Assert.Equal(Path.Combine(h.Dir, ".claude-gone", "skills"), Junction.GetTarget(link)!.TrimEnd('\\'));
    }

    [Fact]
    public void DanglingSharedLink_Reported_AndRepairRemovesIt()
    {
        var (pm, h, _, _) = Make(); using var _h = h;
        pm.Add("work");
        var source = Path.Combine(h.Paths.DefaultHomeDir, "skills");
        Directory.Delete(source);

        var sync = pm.SyncAll();
        var raw = Assert.Single(sync.Problems);
        Assert.Equal($"work: skills is linked to {source}, which no longer exists", raw);
        var p = Assert.Single(sync.ProblemItems);
        Assert.Equal(ProblemFix.RecreateLink, p.Fix);
        Assert.Contains("skills", p.Title);

        var fixedReport = pm.Fix(p);
        Assert.Empty(fixedReport.Problems);
        Assert.False(Directory.Exists(Path.Combine(h.Paths.ProfileDir("work"), "skills")));
        Assert.True(Directory.Exists(h.Paths.DefaultHomeDir));
    }
}
