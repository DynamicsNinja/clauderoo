namespace Clauderoo.Core;

public static class SharedItems
{
    public static readonly string[] Dirs = ["plugins", "skills", "hooks", "agents", "commands", "output-styles"];
    public static readonly string[] Files = ["settings.json", "CLAUDE.md", "keybindings.json"];
    public const string McpServers = "mcpServers";
    public const string Plugins = "plugins";

    public static bool IsDir(string item) => Dirs.Contains(item);

    public static bool IsKnown(string item) => IsDir(item) || Files.Contains(item) || item == McpServers;

    public static bool IsBuiltIn(string item) => IsKnown(item);

    // ---------- with the user's extra shared items (config) ----------

    /// <summary>The built-in shared folders, then the user's extra ones.</summary>
    public static IReadOnlyList<string> DirsOf(ClauderooConfig cfg) =>
        [.. Dirs, .. cfg.ExtraSharedDirs.Where(d => !IsBuiltIn(d)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The built-in shared files, then the user's extra ones.</summary>
    public static IReadOnlyList<string> FilesOf(ClauderooConfig cfg) =>
        [.. Files, .. cfg.ExtraSharedFiles.Where(f => !IsBuiltIn(f)).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static bool IsDir(ClauderooConfig cfg, string item) => DirsOf(cfg).Contains(item);

    public static bool IsFile(ClauderooConfig cfg, string item) => FilesOf(cfg).Contains(item);

    public static bool IsKnown(ClauderooConfig cfg, string item) => IsDir(cfg, item) || IsFile(cfg, item) || item == McpServers;

    /// <summary>Every shared item: folders, files, then mcpServers.</summary>
    public static IReadOnlyList<string> AllOf(ClauderooConfig cfg) => [.. DirsOf(cfg), .. FilesOf(cfg), McpServers];

    public static string UnknownItem(ClauderooConfig cfg, string item) =>
        $"Unknown item '{item}'. Known: {string.Join(", ", AllOf(cfg))}.";

    // ---------- extra shared items: what may be added ----------

    /// <summary>
    /// Things in the Claude folder that belong to one account or one machine: sign-ins, conversations, history and
    /// caches. Sharing them would mix accounts (or leak a sign-in), so they can never be added.
    /// </summary>
    private static readonly string[] NeverShared =
    [
        ".credentials.json", ".claude.json", "projects", "history.jsonl", "sessions", "session-env", "shell-snapshots",
        "todos", "file-history", "statsig", "ide", "debug", "telemetry", "plans", "backups", "cache", "logs",
        "settings.local.json", SyncState.FileName, FilteredDir.MarkerName,
    ];

    /// <summary>Throws unless <paramref name="name"/> is a single file or folder name that may be shared from home.</summary>
    public static void ValidateExtraName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ClauderooException("Give the name of a file or folder in the home profile's folder.");
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or ".." || name.Contains("..")
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != name.Trim() || name.EndsWith('.'))
            throw new ClauderooException(
                $"'{name}' isn't a plain file or folder name. Only items directly inside the home profile's folder can be shared.");
        if (IsBuiltIn(name))
            throw new ClauderooException($"{name} is always shared.");
        if (NeverShared.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(".clauderoo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".bak-", StringComparison.OrdinalIgnoreCase))
            throw new ClauderooException(
                $"{name} holds a sign-in, conversations or other per-account data, so clauderoo won't share it.");
    }

    // ---------- exclusions ----------

    /// <summary>Items whose entries a profile can leave out: the shared folders (plugins included) and mcpServers.</summary>
    public static bool CanExclude(ClauderooConfig cfg, string item) => IsDir(cfg, item) || item == McpServers;

    /// <summary>Splits and checks "item/entry" (e.g. "skills/foo", "mcpServers/github", "plugins/name@marketplace").</summary>
    public static (string Item, string Entry) ParseExclusion(ClauderooConfig cfg, string spec)
    {
        var slash = spec.IndexOf('/');
        if (slash <= 0 || slash == spec.Length - 1)
            throw new ClauderooException(
                $"Name what to leave out as <item>/<entry>, e.g. skills/my-skill or mcpServers/github (got '{spec}').");
        var item = spec[..slash];
        var entry = spec[(slash + 1)..];
        if (!IsKnown(cfg, item)) throw new ClauderooException(UnknownItem(cfg, item));
        if (!CanExclude(cfg, item))
            throw new ClauderooException(
                $"{item} is a single file, so nothing in it can be left out. Use 'cr own <profile> {item}' to keep a profile's own copy.");
        if (entry.IndexOfAny(['/', '\\']) >= 0 || entry is "." or ".." || entry != entry.Trim()
            || (item != McpServers && entry.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            || string.Equals(entry, FilteredDir.MarkerName, StringComparison.OrdinalIgnoreCase))
            throw new ClauderooException($"'{entry}' isn't a single entry of {item}.");
        return (item, entry);
    }

    /// <summary>How entry names of <paramref name="item"/> compare: MCP server names exactly, file names ignoring case.</summary>
    public static StringComparer EntryComparer(string item) =>
        item == McpServers ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The entries of <paramref name="item"/> the profile leaves out.</summary>
    public static HashSet<string> ExclusionsOf(ProfileEntry entry, string item)
    {
        var set = new HashSet<string>(EntryComparer(item));
        foreach (var x in entry.Exclude)
            if (x.Length > item.Length + 1 && x.StartsWith(item + "/", StringComparison.Ordinal))
                set.Add(x[(item.Length + 1)..]);
        return set;
    }
}
