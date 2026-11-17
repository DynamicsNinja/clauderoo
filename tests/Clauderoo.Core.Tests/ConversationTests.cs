using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clauderoo.Cli;

namespace Clauderoo.Core.Tests;

public class ConversationTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0);
    private const string Slug = "C--src-app";
    private const string Id1 = "aaaa1111-0000-0000-0000-000000000001";
    private const string Id2 = "bbbb2222-0000-0000-0000-000000000002";
    private const string Token = "sk-ant-oat01-SECRET-TOKEN-xyz";

    private static (ProfileManager pm, TempHome home, FakeSecretStore tokens) Make()
    {
        var home = new TempHome();
        var tokens = new FakeSecretStore();
        var pm = new ProfileManager(home.Paths, new FakeEnv(), () => Now, new FakeSecretStore(), new FakeGcloud(), tokens)
        { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, home, tokens);
    }

    private static string UserLine(string id, string cwd, string text) =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = text },
            sessionId = id,
            cwd,
            timestamp = "2026-11-17T09:00:00Z",
        });

    private static string Write(string configDir, string id, DateTime whenUtc, params string[] lines)
    {
        var dir = Path.Combine(configDir, "projects", Slug);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(file, whenUtc);
        return file;
    }

    private static (int code, string o, string e, LaunchSpec? inPlace, LaunchSpec? tab) Run(ProfileManager pm, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        LaunchSpec? inPlace = null, tab = null;
        var code = Commands.Run(args, o, e, pm, s => { tab = s; return 0; }, s => { inPlace = s; return 0; },
            wtAvailable: true, claudePath: "claude.cmd", hookCancel: _ => new NoHook());
        return (code, o.ToString(), e.ToString(), inPlace, tab);
    }

    private static string HashTree(string dir)
    {
        if (!Directory.Exists(dir)) return "";
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(f).Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks).Append('|')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(d).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void List_ParsesIdCwdPrompt_NewestFirst()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var cwd = Path.Combine(home.Dir, "src", "app");
        var arrayContent = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "text", text = "  second\n\nprompt  " } } },
            sessionId = Id2, cwd,
        });
        var toolResult = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t", content = "out" } } },
            sessionId = Id1, cwd,
        });
        var longPrompt = "fix   the\tbug " + new string('x', 200);
        Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-2),
            "{\"type\":\"summary\",\"summary\":\"s\"}", toolResult, UserLine(Id1, cwd, longPrompt));
        Write(home.Paths.ProfileDir("work"), Id2, Now.AddHours(-1),
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}", arrayContent);

        var all = ConversationIndex.List(pm);

        Assert.Equal(2, all.Count);
        Assert.Equal(Id2, all[0].Id);
        Assert.Equal("work", all[0].Profile);
        Assert.False(all[0].Isolated);
        Assert.Equal("second prompt", all[0].FirstPrompt);
        Assert.Equal(cwd, all[0].Cwd);
        Assert.Equal(Slug, all[0].Slug);
        Assert.Equal(Id1, all[1].Id);
        Assert.Equal("personal", all[1].Profile);
        Assert.Equal(80, all[1].FirstPrompt!.Length);
        Assert.StartsWith("fix the bug xxx", all[1].FirstPrompt);
        Assert.Equal(new DateTimeOffset(Now.AddHours(-2), TimeSpan.Zero), all[1].When);

        // A profile filter keeps only that profile's conversations.
        var work = ConversationIndex.List(pm, "work");
        Assert.Single(work);
        Assert.Equal(Id2, work[0].Id);
    }

    [Fact]
    public void List_SkipsMalformedLines()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var cwd = Path.Combine(home.Dir, "src");
        Write(home.Paths.DefaultHomeDir, Id1, Now,
            "not json at all", "{\"type\":\"user\",\"message\":", "[1,2,3]", "{\"type\":\"user\",\"message\":{\"content\":42}}",
            UserLine(Id1, cwd, "real prompt"));
        // No sessionId anywhere: the file name gives it.
        Write(home.Paths.DefaultHomeDir, Id2, Now.AddMinutes(-1), "garbage", "{\"type\":\"user\",\"message\":{\"content\":\"hello\"}}");

        var all = ConversationIndex.List(pm);

        Assert.Equal(2, all.Count);
        Assert.Equal("real prompt", all[0].FirstPrompt);
        Assert.Equal(cwd, all[0].Cwd);
        Assert.Equal(Id2, all[1].Id);
        Assert.Null(all[1].Cwd);
        Assert.Equal("hello", all[1].FirstPrompt);
    }

    [Fact]
    public void List_IncludesIsolated()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.IsolatedDir("work"), Id1, Now, UserLine(Id1, home.Dir, "iso"));
        Write(home.Paths.IsolatedDir("personal"), Id2, Now.AddMinutes(-5), UserLine(Id2, home.Dir, "home iso"));

        var all = ConversationIndex.List(pm);

        Assert.Equal(2, all.Count);
        Assert.Equal(("work", true), (all[0].Profile, all[0].Isolated));
        Assert.Equal(("personal", true), (all[1].Profile, all[1].Isolated));
    }

    [Fact]
    public void List_LargeFile_ReadsOnlyHead()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var padding = Enumerable.Range(0, 2000).Select(i =>
            "{\"type\":\"assistant\",\"sessionId\":\"" + Id1 + "\",\"message\":{\"content\":\"" + new string('p', 60) + "\"}}");
        var lines = padding.Append(UserLine(Id1, home.Dir, "too late")).ToArray();
        var file = Write(home.Paths.DefaultHomeDir, Id1, Now, lines);
        Assert.True(new FileInfo(file).Length > 128 * 1024);

        var c = Assert.Single(ConversationIndex.List(pm));
        Assert.Equal(Id1, c.Id);
        Assert.Null(c.Cwd);
        Assert.Null(c.FirstPrompt);
    }

    [Fact]
    public void Copy_Copies_FileAndFolder()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var src = Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "p"));
        var srcFolder = Path.Combine(Path.GetDirectoryName(src)!, Id1);
        Directory.CreateDirectory(Path.Combine(srcFolder, "sub"));
        File.WriteAllText(Path.Combine(srcFolder, "sub", "a.txt"), "A");
        File.WriteAllText(Path.Combine(srcFolder, "b.txt"), "B");
        // A junction inside the folder is skipped, never followed.
        var outside = Path.Combine(home.Dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "S");
        Junction.Create(Path.Combine(srcFolder, "link"), outside);

        var c = Assert.Single(ConversationIndex.List(pm));
        var outcome = ConversationIndex.CopyTo(pm, c, "work", targetIsolated: false);

        Assert.Equal(CopyOutcome.Copied, outcome);
        var tdir = Path.Combine(home.Paths.ProfileDir("work"), "projects", Slug);
        Assert.Equal(File.ReadAllText(src), File.ReadAllText(Path.Combine(tdir, Id1 + ".jsonl")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(tdir, Id1, "sub", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(tdir, Id1, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(tdir, Id1, "link")));
        Assert.Empty(Directory.GetFiles(tdir, "*.tmp*"));

        // Copying again finds the same file: nothing to do.
        Assert.Equal(CopyOutcome.AlreadyUpToDate, ConversationIndex.CopyTo(pm, c, "work", targetIsolated: false));

        // Isolated folders are targets too.
        Assert.Equal(CopyOutcome.Copied, ConversationIndex.CopyTo(pm, c, "work", targetIsolated: true));
        Assert.True(File.Exists(Path.Combine(home.Paths.IsolatedDir("work"), "projects", Slug, Id1 + ".jsonl")));
    }

    [Fact]
    public void Copy_TargetNewer_KeepsTarget()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-2), UserLine(Id1, home.Dir, "old and longer source"));
        var target = Write(home.Paths.ProfileDir("work"), Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "new"));
        var before = File.ReadAllText(target);

        var c = ConversationIndex.List(pm, "personal").Single();
        Assert.Equal(CopyOutcome.AlreadyUpToDate, ConversationIndex.CopyTo(pm, c, "work", false));

        Assert.Equal(before, File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public void Copy_TargetBigger_KeepsTarget()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "s"));
        var target = Write(home.Paths.ProfileDir("work"), Id1, Now.AddHours(-2), UserLine(Id1, home.Dir, "older but much bigger"));

        var c = ConversationIndex.List(pm, "personal").Single();
        Assert.Equal(CopyOutcome.AlreadyUpToDate, ConversationIndex.CopyTo(pm, c, "work", false));
        Assert.Contains("much bigger", File.ReadAllText(target));
    }

    [Fact]
    public void Copy_TargetOlder_BacksUp()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var src = Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "newer and longer"));
        var target = Write(home.Paths.ProfileDir("work"), Id1, Now.AddHours(-2), UserLine(Id1, home.Dir, "old"));
        var old = File.ReadAllText(target);

        var c = ConversationIndex.List(pm, "personal").Single();
        Assert.Equal(CopyOutcome.BackedUpAndCopied, ConversationIndex.CopyTo(pm, c, "work", false));

        Assert.Equal(File.ReadAllText(src), File.ReadAllText(target));
        var bak = target + ".bak-" + Now.ToString("yyyyMMdd-HHmmss");
        Assert.Equal(old, File.ReadAllText(bak));
    }

    [Fact]
    public void Copy_ToHome_Refused()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.ProfileDir("work"), Id1, Now, UserLine(Id1, home.Dir, "p"));
        var before = HashTree(Path.Combine(home.Paths.DefaultHomeDir, "projects"));
        var c = ConversationIndex.List(pm).Single();

        foreach (var iso in new[] { false, true })
        {
            var ex = Assert.Throws<ClauderooException>(() => ConversationIndex.CopyTo(pm, c, "personal", iso));
            Assert.Equal("Conversations can't be copied into the home profile, because clauderoo never writes inside ~/.claude.",
                ex.Message);
        }
        Assert.Equal(before, HashTree(Path.Combine(home.Paths.DefaultHomeDir, "projects")));
        Assert.False(Directory.Exists(home.Paths.IsolatedDir("personal")));
    }

    [Fact]
    public void Copy_NeverWritesSourceHome()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var src = Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "newer source"));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(src)!, Id1));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(src)!, Id1, "x.txt"), "x");
        Write(home.Paths.ProfileDir("work"), Id1, Now.AddHours(-3), UserLine(Id1, home.Dir, "o"));
        var projects = Path.Combine(home.Paths.DefaultHomeDir, "projects");
        var before = HashTree(projects);

        var c = ConversationIndex.List(pm, "personal").Single();
        ConversationIndex.CopyTo(pm, c, "work", false);
        ConversationIndex.CopyTo(pm, c, "work", true);
        Run(pm, "resume", Id1[..8], "--as", "work");

        Assert.Equal(before, HashTree(projects));
    }

    [Fact]
    public void Find_Ambiguous_Throws()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.DefaultHomeDir, "abcd0001-x", Now, UserLine("abcd0001-x", home.Dir, "1"));
        Write(home.Paths.DefaultHomeDir, "abcd0002-y", Now.AddMinutes(-1), UserLine("abcd0002-y", home.Dir, "2"));
        // The same conversation in two profiles is one match; the newest copy wins.
        Write(home.Paths.ProfileDir("work"), "abcd0002-y", Now.AddMinutes(1), UserLine("abcd0002-y", home.Dir, "2"));
        var all = ConversationIndex.List(pm);

        Assert.Throws<ClauderooException>(() => ConversationIndex.Find(all, "abcd"));
        Assert.Throws<ClauderooException>(() => ConversationIndex.Find(all, "zzzz"));
        Assert.Throws<ClauderooException>(() => ConversationIndex.Find(all, ""));
        Assert.Equal("abcd0001-x", ConversationIndex.Find(all, "ABCD0001").Id);
        var y = ConversationIndex.Find(all, "abcd0002");
        Assert.Equal("work", y.Profile);
    }

    [Fact]
    public void Resume_MissingCwd_Refused()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var gone = Path.Combine(home.Dir, "gone");
        Write(home.Paths.DefaultHomeDir, Id1, Now, UserLine(Id1, gone, "p"));

        var r = Run(pm, "resume", Id1[..8], "--as", "work");

        Assert.Equal(1, r.code);
        Assert.Contains($"The folder {gone} no longer exists; Claude can only resume a conversation from its own folder.", r.e);
        Assert.Null(r.inPlace);
        Assert.False(Directory.Exists(Path.Combine(home.Paths.ProfileDir("work"), "projects")));
    }

    [Fact]
    public void Resume_LaunchesWithResumeArg()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var cwd = Path.Combine(home.Dir, "src");
        Directory.CreateDirectory(cwd);
        Write(home.Paths.DefaultHomeDir, Id1, Now, UserLine(Id1, cwd, "p"));

        var r = Run(pm, "resume", Id1[..8], "--as", "work");

        Assert.Equal(0, r.code);
        Assert.NotNull(r.inPlace);
        Assert.Equal(["--resume", Id1], r.inPlace!.Args);
        Assert.Equal(cwd, r.inPlace.WorkingDir);
        Assert.Equal(home.Paths.ProfileDir("work"), r.inPlace.ConfigDirEnv);
        Assert.Equal("work", r.inPlace.Profile);
        Assert.True(File.Exists(Path.Combine(home.Paths.ProfileDir("work"), "projects", Slug, Id1 + ".jsonl")));
    }

    [Fact]
    public void Resume_TokenProfile_GetsToken()
    {
        var (pm, home, _) = Make(); using var _h = home;
        pm.SetProvider("work", Providers.Token, token: Token);
        var cwd = Path.Combine(home.Dir, "src");
        Directory.CreateDirectory(cwd);
        Write(home.Paths.DefaultHomeDir, Id1, Now, UserLine(Id1, cwd, "p"));

        var r = Run(pm, "resume", Id1[..8], "--as", "work");

        Assert.Equal(0, r.code);
        Assert.Equal(Token, r.inPlace!.ExtraEnv!["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.DoesNotContain(Token, r.o + r.e);
    }

    [Fact]
    public void Resume_IsolatedTab_OpensNewWindow()
    {
        var (pm, home, _) = Make(); using var _h = home;
        var cwd = Path.Combine(home.Dir, "src");
        Directory.CreateDirectory(cwd);
        Write(home.Paths.DefaultHomeDir, Id1, Now, UserLine(Id1, cwd, "p"));

        var r = Run(pm, "resume", Id1[..8], "--as", "work", "--isolated", "--tab");

        Assert.Equal(0, r.code);
        Assert.Null(r.inPlace);
        Assert.NotNull(r.tab);
        Assert.Equal(cwd, r.tab!.WorkingDir);
        var cmdLine = string.Join(" ", r.tab.Args);
        Assert.Contains("run work --isolated -- --resume " + Id1, cmdLine);
        Assert.True(File.Exists(Path.Combine(home.Paths.IsolatedDir("work"), "projects", Slug, Id1 + ".jsonl")));
    }

    [Fact]
    public void Resume_IntoHome_Refused()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.ProfileDir("work"), Id1, Now, UserLine(Id1, home.Dir, "p"));

        var r = Run(pm, "resume", Id1[..8], "--as", "personal");

        Assert.Equal(1, r.code);
        Assert.Contains("Conversations can't be copied into the home profile", r.e);
        Assert.Null(r.inPlace);
    }

    [Fact]
    public void Sessions_Json()
    {
        var (pm, home, _) = Make(); using var _h = home;
        Write(home.Paths.DefaultHomeDir, Id1, Now.AddHours(-1), UserLine(Id1, home.Dir, "first prompt"),
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":\"PRIVATE-REPLY\"}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"PRIVATE-SECOND\"}}");
        Write(home.Paths.IsolatedDir("work"), Id2, Now, UserLine(Id2, home.Dir, "iso prompt"));

        var r = Run(pm, "sessions", "--json");

        Assert.Equal(0, r.code);
        using var doc = JsonDocument.Parse(r.o);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var list = root.GetProperty("conversations");
        Assert.Equal(2, list.GetArrayLength());
        var first = list[0];
        Assert.Equal(Id2, first.GetProperty("id").GetString());
        Assert.Equal("work", first.GetProperty("profile").GetString());
        Assert.True(first.GetProperty("isolated").GetBoolean());
        Assert.Equal(home.Dir, first.GetProperty("folder").GetString());
        Assert.Equal("2026-11-17T10:00:00Z", first.GetProperty("when").GetString());
        Assert.Equal("iso prompt", first.GetProperty("firstPrompt").GetString());
        Assert.Equal(["id", "profile", "isolated", "folder", "when", "firstPrompt"],
            first.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("PRIVATE", r.o);
        Assert.DoesNotContain(".jsonl", r.o);

        // A profile filter, and plain text.
        var w = Run(pm, "sessions", "personal");
        Assert.Equal(0, w.code);
        Assert.Contains(Id1[..8], w.o);
        Assert.Contains("first prompt", w.o);
        Assert.DoesNotContain(Id2[..8], w.o);
    }
}
