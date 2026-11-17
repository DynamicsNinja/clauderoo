using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>Where a continued conversation goes: a profile's normal settings folder or its isolated one.</summary>
internal sealed record ContinueTarget(string Profile, bool Isolated, string Label);

/// <summary>
/// The conversation picker's logic without any controls: search and filter, the "Continue as" entries and the row
/// labels. Only the first prompt is ever shown; no other message content is read or displayed.
/// </summary>
internal static class ConversationPicker
{
    /// <summary>Conversations whose profile equals <paramref name="profile"/> (when given) and that contain every word of
    /// <paramref name="search"/> in the prompt, folder, profile name or id (case-insensitive).</summary>
    public static IReadOnlyList<Conversation> Filter(IReadOnlyList<Conversation> all, string? search, string? profile)
    {
        var words = (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return all
            .Where(c => profile == null || c.Profile == profile)
            .Where(c => words.All(w => Contains(c, w)))
            .ToList();
    }

    static bool Contains(Conversation c, string word) =>
        Has(c.FirstPrompt, word) || Has(c.Cwd, word) || Has(c.Profile, word) || Has(c.Id, word);

    static bool Has(string? s, string word) => s != null && s.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every profile, as itself and as "(isolated)". The home profile is only offered isolated, because clauderoo
    /// never writes inside ~/.claude. One dropdown with both kinds of entry is clearer than a checkbox that would
    /// have to be forced on (and disabled) for home.
    /// </summary>
    public static IReadOnlyList<ContinueTarget> Targets(IReadOnlyList<ProfileInfo> profiles)
    {
        var list = new List<ContinueTarget>();
        foreach (var p in profiles)
        {
            if (!p.IsHome) list.Add(new ContinueTarget(p.Name, false, p.Name));
            list.Add(new ContinueTarget(p.Name, true, p.Name + " (isolated)"));
        }
        return list;
    }

    /// <summary>The first target in another profile (the point is to continue it elsewhere), else any other location.</summary>
    public static ContinueTarget? DefaultTarget(IReadOnlyList<ContinueTarget> targets, Conversation c) =>
        targets.FirstOrDefault(t => t.Profile != c.Profile)
        ?? targets.FirstOrDefault(t => t.Isolated != c.Isolated)
        ?? targets.FirstOrDefault();

    /// <summary>Why the conversation can't be continued (same words as <c>cr resume</c>), or null when it can.</summary>
    public static string? Refusal(Conversation c, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrEmpty(c.Cwd))
            return $"Conversation {ConversationIndex.Short(c.Id)} doesn't record its folder; Claude can only resume a conversation from its own folder.";
        if (!directoryExists(c.Cwd))
            return $"The folder {c.Cwd} no longer exists; Claude can only resume a conversation from its own folder.";
        return null;
    }

    /// <summary>"Today 09:05", "Yesterday 23:59", otherwise the date and time. Both arguments are local times.</summary>
    public static string WhenLabel(DateTime when, DateTime now) =>
        when.Date == now.Date ? "Today " + when.ToString("HH:mm")
        : when.Date == now.Date.AddDays(-1) ? "Yesterday " + when.ToString("HH:mm")
        : when.ToString("yyyy-MM-dd HH:mm");

    public static string ProfileLabel(Conversation c) => c.Isolated ? c.Profile + " (isolated)" : c.Profile;

    public static string FolderLabel(Conversation c) => string.IsNullOrEmpty(c.Cwd) ? "?" : c.Cwd;

    public static string PromptLabel(Conversation c) => string.IsNullOrEmpty(c.FirstPrompt) ? "(no text)" : c.FirstPrompt;
}
