using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

public enum SharedKind { Folder, File, McpServers }

/// <summary>How a profile gets a shared item: from home, as its own copy, or from home minus some entries.</summary>
public enum ShareState { Shared, Owned, Excluding }

/// <summary>A shared item in the shared list (<c>cr shared list</c>).</summary>
public record SharedItemInfo(string Name, SharedKind Kind, bool BuiltIn, bool InHome);

/// <summary>One shared item as one profile gets it.</summary>
public record SharedItemStatus(string Item, SharedKind Kind, ShareState State, IReadOnlyList<string> Exclusions,
    bool CanExclude, bool BuiltIn);

public partial class ProfileManager
{
    private static SharedKind KindOf(ClauderooConfig cfg, string item) =>
        item == SharedItems.McpServers ? SharedKind.McpServers
        : SharedItems.IsDir(cfg, item) ? SharedKind.Folder : SharedKind.File;

    // ---------- the shared list ----------

    /// <summary>Every shared item: the built-in ones, then the user's extra ones.</summary>
    public IReadOnlyList<SharedItemInfo> SharedList()
    {
        var cfg = Config;
        return SharedItems.AllOf(cfg).Select(i =>
        {
            var kind = KindOf(cfg, i);
            var path = Path.Combine(cfg.HomeDir, i);
            var inHome = kind switch
            {
                SharedKind.Folder => Directory.Exists(path),
                SharedKind.File => File.Exists(path),
                _ => File.Exists(cfg.HomeJson),
            };
            return new SharedItemInfo(i, kind, SharedItems.IsBuiltIn(i), inHome);
        }).ToList();
    }

    /// <summary>
    /// Shares one more file or folder of the home profile with every profile. <paramref name="kind"/> is needed only
    /// when the item isn't in home yet. A profile that already has its own item of that name keeps it (it owns it).
    /// </summary>
    public SyncReport AddShared(string name, SharedKind? kind = null)
    {
        SharedItems.ValidateExtraName(name);
        var cfg = Config;
        if (SharedItems.IsKnown(cfg, name) || cfg.ExtraSharedDirs.Concat(cfg.ExtraSharedFiles).Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ClauderooException($"{name} is already shared.");
        var path = Path.Combine(cfg.HomeDir, name);
        var isDir = Directory.Exists(path);
        var isFile = !isDir && File.Exists(path);
        if (kind == SharedKind.McpServers) throw new ClauderooException("Only a file or a folder can be added.");
        if (kind == SharedKind.Folder && isFile) throw new ClauderooException($"{name} in {cfg.HomeDir} is a file, not a folder.");
        if (kind == SharedKind.File && isDir) throw new ClauderooException($"{name} in {cfg.HomeDir} is a folder, not a file.");
        var k = kind ?? (isDir ? SharedKind.Folder : isFile ? SharedKind.File
            : throw new ClauderooException($"{name} isn't in {cfg.HomeDir}. Say whether it will be a folder (--dir) or a file (--file)."));

        var notices = new List<string>();
        if (!isDir && !isFile) notices.Add($"{name} isn't in {cfg.HomeDir} yet; profiles get it once it's there.");
        // What a profile already has under that name is its own: it keeps it rather than having it replaced.
        foreach (var (p, e) in cfg.Profiles.Where(kv => !kv.Value.Home))
        {
            var mine = Path.Combine(paths.ProfileDir(p), name);
            if ((Directory.Exists(mine) || File.Exists(mine)) && !e.Overrides.Contains(name))
            {
                e.Overrides.Add(name);
                notices.Add($"{p} keeps its own {name}. Run 'clauderoo share {p} {name}' to use home's instead.");
            }
        }
        (k == SharedKind.Folder ? cfg.ExtraSharedDirs : cfg.ExtraSharedFiles).Add(name);
        ConfigStore.Save(paths, cfg);
        var report = NewSyncer(cfg).SyncAll();
        report.Notices.InsertRange(0, notices);
        return report;
    }

    /// <summary>
    /// Stops sharing an extra item. Each profile's link to home's folder is removed (never what it points to); a copied
    /// file, or a folder the profile owns, stays as the profile's own. Built-in items can't be removed.
    /// </summary>
    public SyncReport RemoveShared(string name)
    {
        var cfg = Config;
        if (SharedItems.IsBuiltIn(name)) throw new ClauderooException($"{name} is built in and is always shared.");
        var dirName = cfg.ExtraSharedDirs.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
        var fileName = cfg.ExtraSharedFiles.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        var item = dirName ?? fileName ?? throw new ClauderooException($"{name} isn't in the shared list.");

        var report = new SyncReport();
        var source = Path.Combine(cfg.HomeDir, item);
        foreach (var (p, e) in cfg.Profiles.Where(kv => !kv.Value.Home))
        {
            if (dirName != null && !e.Overrides.Contains(item))
            {
                var link = Path.Combine(paths.ProfileDir(p), item);
                try
                {
                    var target = Junction.GetTarget(link);
                    if (target != null)
                    {
                        if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(source),
                                StringComparison.OrdinalIgnoreCase))
                            Directory.Delete(link); // non-recursive: removes only the junction
                    }
                    else if (FilteredDir.IsFiltered(link) && !FilteredDir.TryRemove(link, source))
                    {
                        FilteredDir.MakeOwned(link, CopyDir); // nothing left leads into home
                        report.Notices.Add($"{p} keeps its own {item}: it held files of its own.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClauderooException)
                {
                    report.Problems.Add($"{p}: could not unlink {item}: {ex.Message}");
                }
            }
            else if (fileName != null && File.Exists(Path.Combine(paths.ProfileDir(p), item)))
                report.Notices.Add($"{p} keeps its last copy of {item} as its own.");
            e.Overrides.RemoveAll(o => o == item);
            e.Exclude.RemoveAll(x => x.StartsWith(item + "/", StringComparison.Ordinal));
        }
        cfg.ExtraSharedDirs.RemoveAll(d => d == item);
        cfg.ExtraSharedFiles.RemoveAll(f => f == item);
        ConfigStore.Save(paths, cfg);
        return report;
    }

    // ---------- one profile's sharing ----------

    /// <summary>Each shared item as <paramref name="name"/> gets it: shared, owned, or shared minus some entries.</summary>
    public IReadOnlyList<SharedItemStatus> SharingOf(string name)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        return SharedItems.AllOf(cfg).Select(i =>
        {
            var canExclude = SharedItems.CanExclude(cfg, i);
            var ex = canExclude ? SharedItems.ExclusionsOf(entry, i).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
            var state = entry.Home ? ShareState.Shared
                : entry.Overrides.Contains(i) ? ShareState.Owned
                : ex.Count > 0 ? ShareState.Excluding : ShareState.Shared;
            return new SharedItemStatus(i, KindOf(cfg, i), state, ex, canExclude && !entry.Home, SharedItems.IsBuiltIn(i));
        }).ToList();
    }

    /// <summary>
    /// What home has in <paramref name="item"/> that a profile can leave out: MCP server names, plugin ids
    /// ("name@marketplace") or the folder's entries. Only ever reads home.
    /// </summary>
    public IReadOnlyList<string> HomeEntries(string item)
    {
        var cfg = Config;
        if (!SharedItems.CanExclude(cfg, item)) throw new ClauderooException($"Nothing in {item} can be left out.");
        return HomeEntries(cfg, item);
    }

    private static IReadOnlyList<string> HomeEntries(ClauderooConfig cfg, string item)
    {
        if (item == SharedItems.McpServers) return HomeMcpNames(cfg);
        if (item == SharedItems.Plugins) return PluginExclusions.KnownIds(cfg.HomeDir).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var dir = Path.Combine(cfg.HomeDir, item);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> HomeMcpNames(ClauderooConfig cfg)
    {
        try
        {
            if (!File.Exists(cfg.HomeJson)) return [];
            using var fs = new FileStream(cfg.HomeJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return (JsonNode.Parse(fs) as JsonObject)?[SharedItems.McpServers] is JsonObject servers
                ? servers.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static bool InHome(ClauderooConfig cfg, string item, string child) => item switch
    {
        SharedItems.McpServers => HomeMcpNames(cfg).Contains(child, StringComparer.Ordinal),
        SharedItems.Plugins => PluginExclusions.KnownIds(cfg.HomeDir).Any(id => PluginExclusions.Matches(id, child)),
        _ => Directory.Exists(Path.Combine(cfg.HomeDir, item, child)) || File.Exists(Path.Combine(cfg.HomeDir, item, child)),
    };

    /// <summary>Leaves one entry of a shared item out of a profile (<c>cr exclude work skills/foo</c>), then syncs it.</summary>
    public SyncReport Exclude(string name, string spec)
    {
        var cfg = Config;
        var (item, child) = SharedItems.ParseExclusion(cfg, spec);
        var entry = Entry(cfg, name);
        var set = SharedItems.ExclusionsOf(entry, item);
        if (set.Contains(child))
        {
            var r = new SyncReport();
            r.Notices.Add($"{name} already leaves out {item}/{child}.");
            return r;
        }
        set.Add(child);
        return SetExclusions(name, item, set);
    }

    /// <summary>Shares an excluded entry with the profile again (<c>cr include work skills/foo</c>), then syncs it.</summary>
    public SyncReport Include(string name, string spec)
    {
        var cfg = Config;
        var (item, child) = SharedItems.ParseExclusion(cfg, spec);
        var set = SharedItems.ExclusionsOf(Entry(cfg, name), item);
        if (!set.Remove(child)) throw new ClauderooException($"{name} doesn't leave out {item}/{child}.");
        return SetExclusions(name, item, set);
    }

    /// <summary>
    /// Replaces the entries of <paramref name="item"/> that <paramref name="name"/> leaves out, then syncs the profile.
    /// An entry home doesn't have is kept (it stays out if it appears) with a notice.
    /// </summary>
    public SyncReport SetExclusions(string name, string item, IEnumerable<string> excluded)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        if (entry.Home) throw new ClauderooException("The home profile is the source of shared items; it can't leave anything out.");
        if (!SharedItems.IsKnown(cfg, item)) throw new ClauderooException(SharedItems.UnknownItem(cfg, item));
        var wanted = new List<string>();
        foreach (var x in excluded)
        {
            var (_, child) = SharedItems.ParseExclusion(cfg, $"{item}/{x}");
            if (!wanted.Contains(child, SharedItems.EntryComparer(item))) wanted.Add(child);
        }
        var before = SharedItems.ExclusionsOf(entry, item);
        var added = wanted.Where(w => !before.Contains(w)).ToList();
        if (item == SharedItems.Plugins && added.Count > 0 && entry.Overrides.Contains("settings.json"))
            throw new ClauderooException(
                $"{name} owns settings.json, so clauderoo can't turn plugins off for it. Turn them off in its own settings.json " +
                $"(\"enabledPlugins\"), or run 'clauderoo share {name} settings.json' first.");

        var notices = new List<string>();
        foreach (var a in added.Where(a => !InHome(cfg, item, a)))
            notices.Add($"{name}: {item}/{a} isn't in {cfg.HomeProfileName}'s {item} right now; it stays left out if it appears.");
        if (added.Count > 0 && entry.Overrides.Contains(item) && item != SharedItems.Plugins)
            notices.Add($"{name} owns {item}, so this applies only once it shares {item} again.");

        entry.Exclude.RemoveAll(x => x.StartsWith(item + "/", StringComparison.Ordinal));
        entry.Exclude.AddRange(wanted.Order(StringComparer.OrdinalIgnoreCase).Select(w => $"{item}/{w}"));
        ConfigStore.Save(paths, cfg);
        var report = NewSyncer(cfg).Sync(name);
        report.Notices.InsertRange(0, notices);
        return report;
    }
}
