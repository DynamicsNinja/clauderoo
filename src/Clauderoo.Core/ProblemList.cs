using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public enum ProblemFix { ShareAgain, RecreateLink, Resync }

/// <summary>A sync problem in plain language. <see cref="Detail"/> is always the original text, shown on demand.</summary>
public record Problem(string Profile, string Title, string Detail, ProblemFix? Fix);

public static class ProblemTranslator
{
    private static readonly Regex Prefix = new(@"^(?<p>[^:\s]+): (?<rest>.*)$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RealFolder = new(@"^(?<d>\S+) is a real folder, not shared", RegexOptions.Compiled);
    private static readonly Regex FileInWay = new(@"^(?<d>\S+) is a file, not a shared folder", RegexOptions.Compiled);
    private static readonly Regex Dangling = new(@"^(?<d>\S+) is linked to .+, which no longer exists$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WrongLink = new(@"^(?<d>\S+) is linked to .+, not to ", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CouldNotSync = new(@"^could not sync (?<d>\S+): ", RegexOptions.Compiled);

    public static Problem Translate(string raw)
    {
        var m = Prefix.Match(raw);
        if (!m.Success)
            return new Problem("", "Something needs attention", raw, null);
        var profile = m.Groups["p"].Value;
        var rest = m.Groups["rest"].Value;

        Problem P(string title, ProblemFix? fix) => new(profile, title, raw, fix);

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

        if (rest.Contains("not valid JSON") || rest.Contains("is not an object"))
            return P(rest.Contains(".claude.json")
                ? $"A Claude settings file used by {profile} is damaged (tool connections not updated)"
                : $"A Claude settings file used by {profile} is damaged (settings not updated)", null);

        return P($"Something needs attention in {profile}", null);
    }

    /// <summary>Profile and shared item named by a ShareAgain/RecreateLink problem's original text.</summary>
    internal static string? ItemOf(Problem p, string expectedProfile)
    {
        var m = Prefix.Match(p.Detail);
        if (!m.Success || m.Groups["p"].Value != expectedProfile) return null;
        var rest = m.Groups["rest"].Value;
        var x = RealFolder.Match(rest);
        if (!x.Success) x = Dangling.Match(rest);
        if (!x.Success) x = WrongLink.Match(rest);
        return x.Success ? x.Groups["d"].Value : null;
    }
}
