using System.Globalization;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

/// <summary>
/// One removed copy in <c>profiles\_removed</c>: the profile folder (<c>name-yyyyMMdd-HHmmss</c>) and, when the profile
/// had one, its isolated folder (<c>name-yyyyMMdd-HHmmss-isolated</c>), moved aside together by one remove.
/// </summary>
/// <param name="Id">The removal timestamp in the folder names (yyyyMMdd-HHmmss); with the name it picks one copy.</param>
/// <param name="RemovedAt">When it was removed (local time, from the folder name).</param>
/// <param name="Size">Bytes in its folders, not counting anything behind a junction.</param>
/// <param name="ProfileDir">The profile folder, or null when only the isolated folder was moved aside.</param>
public record RemovedCopy(string Name, string Id, DateTime RemovedAt, long Size, string? ProfileDir, string? IsolatedDir)
{
    /// <summary>Whether <see cref="ProfileManager.Restore(string, string?)"/> can bring it back (it needs the profile folder).</summary>
    public bool CanRestore => ProfileDir != null;
}

/// <summary>What <see cref="ProfileManager.Purge"/> deleted, what it couldn't, and the bytes freed.</summary>
public record PurgeResult(IReadOnlyList<RemovedCopy> Deleted, IReadOnlyList<PurgeFailure> Failed, long Freed);

public record PurgeFailure(RemovedCopy Copy, string Message);

public static partial class RemovedProfiles
{
    internal const string StampFormat = "yyyyMMdd-HHmmss";

    [GeneratedRegex(@"^(?<name>[a-z0-9-]+)-(?<id>\d{8}-\d{6})(?<iso>-isolated)?\z")]
    internal static partial Regex FolderPattern();

    [GeneratedRegex(@"^\d{8}-\d{6}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?<n>\d+)\s*(?<u>[mhdw])\z", RegexOptions.IgnoreCase)]
    private static partial Regex AgePattern();

    /// <summary>Parses an age such as <c>30d</c>, <c>2w</c>, <c>12h</c> or <c>90m</c> (minutes).</summary>
    public static TimeSpan ParseAge(string text)
    {
        var m = AgePattern().Match(text?.Trim() ?? "");
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new ClauderooException($"Invalid age '{text}'. Use a number and a unit, e.g. 30d (days), 2w (weeks), 12h (hours) or 90m (minutes).");
        return char.ToLowerInvariant(m.Groups["u"].Value[0]) switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromDays(7.0 * n),
        };
    }

    /// <summary>
    /// The copy id in <paramref name="at"/>: a timestamp (<c>20261118-142501</c>) or a folder name
    /// (<c>work-20261118-142501</c>) as <c>cr removed</c> shows them.
    /// </summary>
    public static string NormalizeId(string name, string at)
    {
        var id = at.Trim();
        if (id.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)) id = id[(name.Length + 1)..];
        if (id.EndsWith("-isolated", StringComparison.OrdinalIgnoreCase)) id = id[..^"-isolated".Length];
        if (!IdPattern().IsMatch(id))
            throw new ClauderooException($"'{at}' isn't a removed copy. Run 'clauderoo removed' to see them (the id looks like 20261118-142501).");
        return id;
    }

    /// <summary>A size for people: 0 B, 812 KB, 12.3 MB, 1.4 GB.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        var fmt = u == 0 || v >= 100 ? "0" : "0.#";
        return v.ToString(fmt, CultureInfo.InvariantCulture) + " " + units[u];
    }
}

public partial class ProfileManager
{
    /// <summary>
    /// The removed copies in <c>profiles\_removed</c> (all, or those of <paramref name="name"/>), by name and then
    /// newest first. Folders clauderoo didn't put there are ignored.
    /// </summary>
    public IReadOnlyList<RemovedCopy> ListRemoved(string? name = null)
    {
        if (!Directory.Exists(paths.RemovedDir)) return [];
        var groups = new Dictionary<(string Name, string Id), (string? Profile, string? Isolated)>();
        foreach (var d in Directory.GetDirectories(paths.RemovedDir))
        {
            var m = RemovedProfiles.FolderPattern().Match(Path.GetFileName(d));
            if (!m.Success) continue;
            var key = (m.Groups["name"].Value, m.Groups["id"].Value);
            if (name != null && key.Item1 != name) continue;
            if (!DateTime.TryParseExact(key.Item2, RemovedProfiles.StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out _)) continue;
            groups.TryGetValue(key, out var g);
            groups[key] = m.Groups["iso"].Success ? (g.Profile, d) : (d, g.Isolated);
        }
        return groups
            .Select(kv =>
            {
                var at = DateTime.ParseExact(kv.Key.Id, RemovedProfiles.StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                var size = (kv.Value.Profile is { } p ? SafeTree.Size(p) : 0) + (kv.Value.Isolated is { } i ? SafeTree.Size(i) : 0);
                return new RemovedCopy(kv.Key.Name, kv.Key.Id, at, size, kv.Value.Profile, kv.Value.Isolated);
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenByDescending(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One removed copy of <paramref name="name"/>: the one removed at <paramref name="at"/> (an id or folder name, see
    /// <see cref="RemovedProfiles.NormalizeId"/>), or the newest that can be restored. Throws when there is none.
    /// </summary>
    public RemovedCopy FindRemoved(string name, string? at = null)
    {
        ProfileName.Validate(name);
        var copies = ListRemoved(name);
        if (at == null)
            return copies.FirstOrDefault(c => c.CanRestore)
                   ?? throw new ClauderooException($"No removed profile named '{name}' was found.");
        var id = RemovedProfiles.NormalizeId(name, at);
        return copies.FirstOrDefault(c => c.Id == id)
               ?? throw new ClauderooException($"No copy of '{name}' removed at {id} was found. Run 'clauderoo removed' to see them.");
    }

    /// <summary>
    /// The removed copies a purge would delete: those of <paramref name="name"/> (all of them, or the one removed at
    /// <paramref name="at"/>), every copy (<paramref name="all"/>), narrowed to those removed at least
    /// <paramref name="olderThan"/> ago. Something must say which: a name, an age or <paramref name="all"/>.
    /// </summary>
    public IReadOnlyList<RemovedCopy> SelectRemoved(string? name, string? at = null, TimeSpan? olderThan = null, bool all = false)
    {
        if (name == null && olderThan == null && !all)
            throw new ClauderooException("Say which removed copies: a profile name, --older-than <age> or --all.");
        if (name != null && all)
            throw new ClauderooException("Give a profile name or --all, not both.");
        if (at != null && name == null)
            throw new ClauderooException("--at needs a profile name.");
        if (name != null) ProfileName.Validate(name);
        IEnumerable<RemovedCopy> copies = at != null ? [FindRemoved(name!, at)] : ListRemoved(name);
        if (olderThan is { } age)
        {
            var cutoff = now() - age;
            copies = copies.Where(c => c.RemovedAt <= cutoff);
        }
        return copies.ToList();
    }

    /// <summary>
    /// Permanently deletes removed copies (both folders of each). The one place clauderoo really deletes a profile.
    /// Each copy is looked up again in <c>_removed</c> by name and id, so only folders there are ever deleted, and a
    /// junction inside is removed as a link only (see <see cref="SafeTree.Delete"/>): its target is never touched.
    /// Carries on past a copy that can't be deleted and reports it.
    /// </summary>
    public PurgeResult Purge(IEnumerable<RemovedCopy> copies)
    {
        var current = ListRemoved().ToDictionary(c => (c.Name, c.Id));
        var deleted = new List<RemovedCopy>();
        var failed = new List<PurgeFailure>();
        long freed = 0;
        foreach (var wanted in copies.DistinctBy(c => (c.Name, c.Id)))
        {
            if (!current.TryGetValue((wanted.Name, wanted.Id), out var copy)) continue; // already gone
            try
            {
                foreach (var dir in new[] { copy.IsolatedDir, copy.ProfileDir })
                    if (dir != null)
                    {
                        RefuseOutsideRemoved(dir);
                        SafeTree.Delete(dir);
                    }
                deleted.Add(copy);
                freed += copy.Size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
            {
                failed.Add(new PurgeFailure(copy, $"Couldn't delete the copy of {copy.Name} removed at {copy.Id}: {e.Message}"));
            }
        }
        return new PurgeResult(deleted, failed, freed);
    }

    private void RefuseOutsideRemoved(string dir)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\', '/'));
        if (parent == null || !string.Equals(parent.TrimEnd('\\', '/'), Path.GetFullPath(paths.RemovedDir).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)
            || !RemovedProfiles.FolderPattern().IsMatch(Path.GetFileName(dir)))
            throw new ClauderooException($"Refusing to delete {dir}: it isn't a removed copy.");
    }
}
