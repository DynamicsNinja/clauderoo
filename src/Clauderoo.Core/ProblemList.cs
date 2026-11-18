using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public enum ProblemFix { ShareAgain, RecreateLink, Resync, KeepOwn }

/// <summary>
/// A sync problem in plain language. <see cref="Detail"/> is always the original text, shown on demand.
/// <see cref="Hint"/>, when set, is the plain explanation (what happened, what to do, what the fix does).
/// </summary>
public record Problem(string Profile, string Title, string Detail, ProblemFix? Fix, string? Hint = null);

public static class ProblemTranslator
{
    /// <summary>The title of a problem the translator doesn't know (with " in {profile}" when it names one).</summary>
    public const string GenericTitle = "Something needs attention";

    private static readonly Regex Prefix = new(@"^(?<p>[^:\s]+): (?<rest>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RealFolder = new(@"^(?<d>\S+) is a real folder, not shared", RegexOptions.Compiled);
    private static readonly Regex FileInWay = new(@"^(?<d>\S+) is a file, not a shared folder", RegexOptions.Compiled);
    private static readonly Regex Dangling = new(@"^(?<d>\S+) is linked to .+, which no longer exists$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WrongLink = new(@"^(?<d>\S+) is linked to .+, not to ", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CouldNotSync = new(@"^could not sync (?<d>\S+): ", RegexOptions.Compiled);
    private static readonly Regex OwnEntry =
        new($@"^(?<i>[^\\\s]+)\\(?<n>\S+) {Regex.Escape(FilteredDir.OwnEntryText)} ", RegexOptions.Compiled);
    private static readonly Regex MixedFolder = new($@"^(?<d>\S+) {Regex.Escape(Syncer.MixedFolderText)}", RegexOptions.Compiled);
    private static readonly Regex LinksNotMoved =
        new($@"^(?<d>\S+) {Regex.Escape(ProfileManager.LinksNotMovedText)} ", RegexOptions.Compiled);
    private static readonly Regex CouldNotUnlink =
        new($@"^{Regex.Escape(ProfileManager.CouldNotUnlinkText)} (?<d>\S+): ", RegexOptions.Compiled);

    private const string SettingsFile = "settings.json";

    public static Problem Translate(string raw)
    {
        var m = Prefix.Match(raw);
        if (!m.Success)
            return new Problem("", GenericTitle, raw, null);
        var profile = m.Groups["p"].Value;
        var rest = m.Groups["rest"].Value;

        Problem P(string title, ProblemFix? fix, string? hint = null) => new(profile, title, raw, fix, hint);

        if (RealFolder.Match(rest) is { Success: true } r)
            return P($"{profile} keeps its own copy of {r.Groups["d"].Value}, so it isn't shared", ProblemFix.ShareAgain);
        if (FileInWay.Match(rest) is { Success: true } f)
            return P($"A file is blocking the shared {f.Groups["d"].Value} folder in {profile}", null);
        if (rest.StartsWith(Syncer.HomeMissingText))
            return P($"{profile} can't reach the home Claude folder: it was moved or deleted", null);
        if (Dangling.Match(rest) is { Success: true } g)
            return P($"{profile}'s {g.Groups["d"].Value} points to a home folder that no longer exists", ProblemFix.RecreateLink);
        if (WrongLink.Match(rest) is { Success: true } l)
            return P($"{profile}'s {l.Groups["d"].Value} points to the wrong place", ProblemFix.RecreateLink);
        if (rest.StartsWith("could not read sync state"))
            return P($"Couldn't read {profile}'s sync information", ProblemFix.Resync);
        if (rest.StartsWith("could not save sync state"))
            return P($"Couldn't save {profile}'s sync information", ProblemFix.Resync);
        if (rest.StartsWith("could not sync mcpServers"))
            return P($"Couldn't update {profile}'s tool connections", ProblemFix.Resync);
        if (CouldNotSync.Match(rest) is { Success: true } c)
            return P($"Couldn't update {c.Groups["d"].Value} in {profile}", ProblemFix.Resync);
        if (rest.StartsWith("could not sync:"))
            return P($"Couldn't update {profile}", ProblemFix.Resync);
        if (rest.StartsWith("could not save the model Claude chose"))
            return P($"Couldn't remember the model Claude chose in {profile}", ProblemFix.Resync);
        if (rest.Contains("kept changing"))
            return P(rest.Contains("MCP")
                ? $"{profile}'s tool connections are being changed by another program"
                : $"{profile}'s settings are being changed by another program", ProblemFix.Resync);

        if (OwnEntry.Match(rest) is { Success: true } o)
        {
            var (item, entry) = (o.Groups["i"].Value, o.Groups["n"].Value);
            return P($"{profile} uses its own {item}\\{entry} instead of home's", null,
                $"{profile} has its own {entry} in its {item} folder, so the one from the home Claude folder isn't used there. " +
                $"If that's what you want, there's nothing to do. To use home's instead, move {profile}'s own {entry} out " +
                $"of its {item} folder; clauderoo shares home's there on its next sync.");
        }
        if (MixedFolder.Match(rest) is { Success: true } x)
        {
            var d = x.Groups["d"].Value;
            return P($"{profile}'s {d} folder has files of its own mixed in with the shared ones", ProblemFix.KeepOwn,
                $"{profile} no longer leaves anything out of {d}, but its {d} folder holds files of its own, so it can't go " +
                $"back to being one shared folder. Keep its own copy makes {d} {profile}'s own: what it shares from home is " +
                $"copied in, nothing is deleted, and later changes to home's {d} no longer reach {profile}. " +
                $"Or move {profile}'s own files out of {d}; clauderoo then shares it again on its next sync.");
        }
        if (LinksNotMoved.Match(rest) is { Success: true } n)
        {
            var d = n.Groups["d"].Value;
            return P($"{profile}'s {d} still uses the old home Claude folder", null,
                $"The home Claude folder moved, but {profile}'s {d} folder holds files of its own, so clauderoo left it as it " +
                $"was: it still uses {d} from the old home folder. Nothing was deleted. Copy the details and send them to " +
                "whoever helps you with clauderoo.");
        }
        if (CouldNotUnlink.Match(rest) is { Success: true } u)
        {
            var d = u.Groups["d"].Value;
            return P($"Couldn't stop sharing {d} with {profile}", null,
                $"{d} is no longer shared, but clauderoo couldn't remove {profile}'s link to home's {d}, so {profile} may " +
                "still see it. Close any Claude sessions using it, then copy the details and send them to whoever helps you " +
                "with clauderoo.");
        }
        if (rest.StartsWith(Syncer.PluginsStayOnText))
            return P($"Plugins {profile} leaves out stay on, because it has its own settings", ProblemFix.ShareAgain,
                $"{profile} keeps its own {SettingsFile}, so clauderoo can't turn off the plugins it leaves out. " +
                $"Share it again switches {profile} to the shared {SettingsFile} with those plugins off; clauderoo keeps a " +
                $"backup of {profile}'s own. Or turn the plugins off yourself in {profile}'s own {SettingsFile}.");
        if (rest.StartsWith(Syncer.EnabledPluginsDamagedText))
            return P($"A Claude settings file used by {profile} is damaged (plugins it leaves out not turned off)", null);

        if (rest.Contains("not valid JSON") || rest.Contains("is not an object"))
            return P(rest.Contains(".claude.json")
                ? $"A Claude settings file used by {profile} is damaged (tool connections not updated)"
                : $"A Claude settings file used by {profile} is damaged (settings not updated)", null);

        return P($"{GenericTitle} in {profile}", null);
    }

    /// <summary>True for a problem the translator didn't recognise (shown under the generic title).</summary>
    public static bool IsGeneric(Problem p) => p.Title.StartsWith(GenericTitle, StringComparison.Ordinal);

    /// <summary>Profile and shared item named by a ShareAgain/RecreateLink/KeepOwn problem's original text.</summary>
    internal static string? ItemOf(Problem p, string expectedProfile)
    {
        var m = Prefix.Match(p.Detail);
        if (!m.Success || m.Groups["p"].Value != expectedProfile) return null;
        var rest = m.Groups["rest"].Value;
        if (p.Fix == ProblemFix.ShareAgain && rest.StartsWith(Syncer.PluginsStayOnText)) return SettingsFile;
        var x = p.Fix == ProblemFix.KeepOwn ? MixedFolder.Match(rest) : RealFolder.Match(rest);
        if (!x.Success && p.Fix != ProblemFix.KeepOwn) x = Dangling.Match(rest);
        if (!x.Success && p.Fix != ProblemFix.KeepOwn) x = WrongLink.Match(rest);
        return x.Success ? x.Groups["d"].Value : null;
    }
}
