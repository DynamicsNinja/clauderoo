using System.Reflection;
using System.Text.RegularExpressions;
using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

// Every problem a sync (or a sharing change) can report gets its own plain title in the tray, never the generic one.
public class ProblemTextTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0);

    private static (ProfileManager pm, TempHome h, string home, string work) Make()
    {
        var h = new TempHome();
        var pm = new ProfileManager(h.Paths, new FakeEnv(), () => Now) { HelperExe = @"C:\x\clauderoo.exe" };
        pm.Init();
        pm.Add("work");
        return (pm, h, h.Paths.DefaultHomeDir, h.Paths.ProfileDir("work"));
    }

    private static void Put(string file, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // ---------- every producer ----------

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "clauderoo.slnx"))) return d.FullName;
        throw new InvalidOperationException("clauderoo.slnx not found above the test folder.");
    }

    private static readonly Regex AddCall = new(@"Problems\.Add\((?<arg>.*?)\);\s*$", RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex Literal = new(@"\$?""(?<s>(?:[^""\\]|\\.)*)""", RegexOptions.Singleline);
    private static readonly Regex Hole = new(@"\{(?<e>[^{}]+)\}");
    private static readonly Regex HelperCall = new(@"^\s*(?<m>\w+)\(");

    // Internal consts of Core (HomeMissingText, MixedFolderText…) by name, so a hole naming one gets its real text.
    private static readonly Dictionary<string, string> Consts = typeof(Syncer).Assembly.GetTypes()
        .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .GroupBy(f => f.Name).Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => (string)g.Single().GetRawConstantValue()!);

    /// <summary>The text one Problems.Add(...) argument produces, with "work" for every value that isn't a const.</summary>
    private static string Render(string arg)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Match lit in Literal.Matches(arg))
        {
            var s = lit.Groups["s"].Value.Replace(@"\\", "\u0001").Replace("\\\"", "\"").Replace("\u0001", @"\");
            if (lit.Value.StartsWith('$'))
                s = Hole.Replace(s, h => Consts.TryGetValue(h.Groups["e"].Value.Trim(), out var c) ? c : "work");
            sb.Append(s);
        }
        return sb.ToString();
    }

    /// <summary>Every problem text a Problems.Add in src/ can produce (helper methods like OwnEntryProblem followed).</summary>
    public static TheoryData<string, string> ProducedProblems()
    {
        var data = new TheoryData<string, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            foreach (Match m in AddCall.Matches(code))
            {
                var arg = m.Groups["arg"].Value;
                if (!Literal.IsMatch(arg) && HelperCall.Match(arg) is { Success: true } h)
                {
                    // A helper that builds the text: `string Name(...) => $"...";`
                    var body = new Regex($@"string {h.Groups["m"].Value}\([^)]*\)\s*=>(?<b>.*?);\s*$",
                        RegexOptions.Multiline | RegexOptions.Singleline).Match(code);
                    Assert.True(body.Success, $"{Path.GetFileName(file)}: can't follow Problems.Add({arg})");
                    arg = body.Groups["b"].Value;
                }
                if (seen.Add(Render(arg))) data.Add(Path.GetFileName(file), Render(arg));
            }
        }
        return data;
    }

    [Fact]
    public void ProducedProblems_AreFound()
    {
        var all = ProducedProblems().Select(r => (string)r[1]).ToList();
        Assert.True(all.Count >= 20, $"only {all.Count} producers found");
        Assert.All(all, p => Assert.StartsWith("work: ", p));
        Assert.Contains(all, p => p.Contains(Syncer.HomeMissingText));
        Assert.Contains(all, p => p.Contains(@"work\work " + FilteredDir.OwnEntryText));
    }

    [Theory]
    [MemberData(nameof(ProducedProblems))]
    public void EveryProducedProblem_HasItsOwnTitle(string file, string raw)
    {
        var p = ProblemTranslator.Translate(raw);
        Assert.False(ProblemTranslator.IsGeneric(p), $"{file}: no translation for \"{raw}\"");
        Assert.Equal("work", p.Profile);
        Assert.Equal(raw, p.Detail);
        Assert.DoesNotContain("clauderoo ", p.Title);
        var text = TrayText.Explain(p);
        Assert.DoesNotContain("run '", text);
        Assert.DoesNotContain("'clauderoo", text);
    }

    // ---------- the new translations ----------

    [Fact]
    public void OwnEntry_NamesTheEntry_NoFix()
    {
        var p = ProblemTranslator.Translate(@"work: skills\mine is the profile's own, so skills\mine from home isn't shared");
        Assert.Equal(@"work uses its own skills\mine instead of home's", p.Title);
        Assert.Null(p.Fix);
        Assert.Contains("move work's own mine out of its skills folder", TrayText.Explain(p));
    }

    [Fact]
    public void MixedFolder_OffersKeepOwn()
    {
        var p = ProblemTranslator.Translate("work: skills holds files of its own next to the shared ones, so it can't go back to " +
                                            "being one shared folder; move them out, or run 'clauderoo own work skills'");
        Assert.Equal(ProblemFix.KeepOwn, p.Fix);
        Assert.Equal("work's skills folder has files of its own mixed in with the shared ones", p.Title);
        Assert.Equal("&Keep its own copy", TrayText.FixLabel(ProblemFix.KeepOwn));
        Assert.Contains("nothing is deleted", TrayText.Explain(p));
        Assert.Equal("skills", ProblemTranslator.ItemOf(p, "work"));
    }

    [Fact]
    public void LinksNotMoved_NoFix()
    {
        var p = ProblemTranslator.Translate(@"work: skills holds files of its own, so its links into C:\old\.claude weren't moved");
        Assert.Equal("work's skills still uses the old home Claude folder", p.Title);
        Assert.Null(p.Fix);
        Assert.Contains("Nothing was deleted", TrayText.Explain(p));
    }

    [Fact]
    public void CouldNotUnlink_NoFix()
    {
        var p = ProblemTranslator.Translate("work: could not unlink prompts: Access to the path is denied.");
        Assert.Equal("Couldn't stop sharing prompts with work", p.Title);
        Assert.Null(p.Fix);
        Assert.DoesNotContain("Access", p.Title);
    }

    [Fact]
    public void PluginsStayOn_OffersShareSettings()
    {
        var p = ProblemTranslator.Translate("work: the plugins it excludes stay on, because it owns settings.json; turn them " +
                                            "off in its settings.json, or run 'clauderoo share work settings.json'");
        Assert.Equal(ProblemFix.ShareAgain, p.Fix);
        Assert.Equal("Plugins work leaves out stay on, because it has its own settings", p.Title);
        Assert.Equal("settings.json", ProblemTranslator.ItemOf(p, "work"));
        Assert.Contains("keeps a backup", TrayText.Explain(p));
    }

    [Fact]
    public void EnabledPluginsDamaged_IsADamagedFile()
    {
        var p = ProblemTranslator.Translate("work: home settings.json \"enabledPlugins\" is not an object; excluded plugins not turned off");
        Assert.Null(p.Fix);
        Assert.Contains("damaged", p.Title);
        Assert.Contains("plugins", p.Title);
        Assert.DoesNotContain("enabledPlugins", p.Title);
    }

    [Fact]
    public void Hint_ReplacesTheGenericExplanation_OldProblemsUnchanged()
    {
        Assert.Equal("h", TrayText.Explain(new Problem("w", "t", "w: x", null, "h")));
        var old = ProblemTranslator.Translate(@"work: skills is linked to C:\a, not to C:\b");
        Assert.Null(old.Hint);
        Assert.StartsWith("Repair the link", TrayText.Explain(old));
    }

    // ---------- the fixes ----------

    [Fact]
    public void Fix_KeepOwn_MakesTheFolderOwn_KeepsItsFiles()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        Put(Path.Combine(home, "skills", "a", "SKILL.md"), "a");
        Put(Path.Combine(home, "skills", "b", "SKILL.md"), "b");
        pm.Exclude("work", "skills/a");
        var mine = Path.Combine(work, "skills", "mine", "SKILL.md");
        Put(mine, "mine");

        var problem = Assert.Single(pm.Include("work", "skills/a").ProblemItems, p => p.Fix == ProblemFix.KeepOwn);
        var after = pm.Fix(problem);

        Assert.Empty(after.Problems);
        Assert.Equal("mine", File.ReadAllText(mine));
        Assert.Contains("skills", pm.Config.Profiles["work"].Overrides);
        Assert.Null(Junction.GetTarget(Path.Combine(work, "skills")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(work, "skills", "a", "SKILL.md")));
        Assert.Null(Junction.GetTarget(Path.Combine(work, "skills", "a"))); // copied, not linked into home
        Assert.Equal("a", File.ReadAllText(Path.Combine(home, "skills", "a", "SKILL.md")));
    }

    [Fact]
    public void Fix_KeepOwn_ValidatesTheFolder()
    {
        var (pm, h, _, _) = Make();
        using var _h = h;
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t",
            "work: settings.json holds files of its own next to the shared ones", ProblemFix.KeepOwn)));
        Assert.Throws<ClauderooException>(() => pm.Fix(new Problem("work", "t",
            "work: skills is a real folder, not shared", ProblemFix.KeepOwn)));
    }

    [Fact]
    public void Fix_PluginsStayOn_SharesSettingsAgain()
    {
        var (pm, h, home, work) = Make();
        using var _ = h;
        pm.Exclude("work", "plugins/foo@market");
        pm.Own("work", "settings.json");

        var problem = Assert.Single(pm.Sync("work").ProblemItems, p => p.Detail.Contains(Syncer.PluginsStayOnText));
        Assert.Equal(ProblemFix.ShareAgain, problem.Fix);
        var after = pm.Fix(problem);

        Assert.DoesNotContain(after.Problems, p => p.Contains(Syncer.PluginsStayOnText));
        Assert.DoesNotContain("settings.json", pm.Config.Profiles["work"].Overrides);
    }
}
