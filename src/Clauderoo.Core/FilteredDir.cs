using System.Security.Cryptography;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>
/// A shared folder with exclusions. Instead of one junction to home's folder, the profile gets a real folder that holds
/// one junction per sub-folder of home's folder and a copy of each file, leaving the excluded entries out. A marker
/// file records what clauderoo put there, so only those entries are ever removed: anything else in the folder is the
/// profile's own and is left alone. Nothing here ever writes inside home's folder, and links are only ever removed
/// non-recursively.
/// </summary>
public static class FilteredDir
{
    public const string MarkerName = ".clauderoo-filtered.json";

    /// <summary>The stable part of the problem for an entry the profile has of its own (see ProblemTranslator).</summary>
    internal const string OwnEntryText = "is the profile's own, so";

    private static string OwnEntryProblem(string profile, string item, string n) =>
        $"{profile}: {item}\\{n} {OwnEntryText} {item}\\{n} from home isn't shared";

    private const string Note =
        "clauderoo keeps this folder in step with the home profile's, leaving out what this profile excludes. " +
        "Entries listed here are clauderoo's; anything else in the folder is the profile's own.";

    private sealed class Marker
    {
        public string Note { get; set; } = FilteredDir.Note;
        /// <summary>Sub-folders linked (junction) to home's.</summary>
        public List<string> Links { get; set; } = [];
        /// <summary>Files copied from home, with the hash clauderoo last wrote.</summary>
        public Dictionary<string, string> Copies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>True when <paramref name="path"/> is a real folder that clauderoo keeps filtered.</summary>
    public static bool IsFiltered(string path) =>
        Directory.Exists(path) && Junction.GetTarget(path) == null && File.Exists(Path.Combine(path, MarkerName));

    private static Marker Read(string dir)
    {
        var file = Path.Combine(dir, MarkerName);
        try
        {
            var m = File.Exists(file) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(file), Json) : null;
            m ??= new Marker();
            m.Links ??= [];
            m.Copies = new Dictionary<string, string>(m.Copies ?? new(), StringComparer.OrdinalIgnoreCase);
            return m;
        }
        catch (JsonException) { return new Marker(); } // a damaged marker: nothing is clauderoo's, so nothing is removed
    }

    private static void Write(string dir, Marker m) =>
        AtomicFile.WriteAllText(Path.Combine(dir, MarkerName), JsonSerializer.Serialize(m, Json));

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings <paramref name="dir"/> (the profile's folder: missing, a whole-folder junction to <paramref name="source"/>,
    /// or already filtered) to home's entries minus <paramref name="excluded"/>. The caller has checked that anything
    /// already at <paramref name="dir"/> is one of those three.
    /// </summary>
    public static void Sync(string profile, string item, string dir, string source, ISet<string> excluded, string stamp,
        SyncReport report)
    {
        if (Junction.GetTarget(dir) != null) Directory.Delete(dir); // non-recursive: removes only the junction
        var fresh = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        var m = fresh ? new Marker() : Read(dir);
        if (fresh) Write(dir, m); // the folder is marked as clauderoo's before anything goes in

        var wantDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wantFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.GetDirectories(source))
            if (!excluded.Contains(Path.GetFileName(d))) wantDirs.Add(Path.GetFileName(d));
        foreach (var f in Directory.GetFiles(source))
        {
            var n = Path.GetFileName(f);
            if (!excluded.Contains(n) && !string.Equals(n, MarkerName, StringComparison.OrdinalIgnoreCase)) wantFiles.Add(n);
        }

        try
        {
            // What clauderoo put here and no longer belongs: links go (non-recursively), unchanged copies go, changed
            // copies are kept aside.
            foreach (var n in m.Links.ToList())
            {
                if (wantDirs.Contains(n)) continue;
                var p = Path.Combine(dir, n);
                if (Junction.GetTarget(p) != null) Directory.Delete(p);
                m.Links.Remove(n);
            }
            foreach (var (n, hash) in m.Copies.ToList())
            {
                if (wantFiles.Contains(n)) continue;
                RemoveCopy(profile, item, dir, n, hash, stamp, report);
                m.Copies.Remove(n);
            }

            foreach (var n in wantDirs)
            {
                var p = Path.Combine(dir, n);
                var src = Path.Combine(source, n);
                var target = Junction.GetTarget(p);
                if (SamePath(target, src))
                {
                    if (!m.Links.Contains(n, StringComparer.OrdinalIgnoreCase)) m.Links.Add(n);
                    continue;
                }
                if (target != null || Directory.Exists(p) || File.Exists(p))
                {
                    report.Problems.Add(OwnEntryProblem(profile, item, n));
                    continue;
                }
                Junction.Create(p, src); // new, or put back after a remove/restore unlinked it
                if (!m.Links.Contains(n, StringComparer.OrdinalIgnoreCase)) m.Links.Add(n);
            }

            foreach (var n in wantFiles)
            {
                var p = Path.Combine(dir, n);
                var src = Path.Combine(source, n);
                var srcHash = Hash(src);
                if (m.Copies.TryGetValue(n, out var recorded))
                {
                    if (File.Exists(p))
                    {
                        var cur = Hash(p);
                        if (cur == srcHash) { m.Copies[n] = srcHash; continue; }
                        if (cur != recorded)
                        {
                            var backup = $"{n}.bak-{stamp}";
                            File.Move(p, Path.Combine(dir, backup), overwrite: true);
                            report.Notices.Add(
                                $"{profile}: your changes to {item}\\{n} were replaced by home's (saved {item}\\{backup}). " +
                                $"Use 'clauderoo own {profile} {item}' to keep {profile}'s own.");
                        }
                    }
                    else if (Directory.Exists(p))
                    {
                        m.Copies.Remove(n);
                        report.Problems.Add(OwnEntryProblem(profile, item, n));
                        continue;
                    }
                }
                else if (File.Exists(p) || Directory.Exists(p))
                {
                    if (File.Exists(p) && Hash(p) == srcHash) { m.Copies[n] = srcHash; continue; } // an earlier copy
                    report.Problems.Add(OwnEntryProblem(profile, item, n));
                    continue;
                }
                Copy(src, p);
                m.Copies[n] = srcHash;
            }
        }
        finally
        {
            Write(dir, m);
        }
    }

    /// <summary>
    /// Turns a filtered folder back into nothing (the caller then links the whole folder): removes clauderoo's links and
    /// unchanged copies, then the empty folder. Returns false, changing nothing, when the folder holds anything of the
    /// profile's own (a changed copy, or something clauderoo didn't put there).
    /// </summary>
    public static bool TryRemove(string dir, string source)
    {
        var m = Read(dir);
        var mine = new List<string>();
        foreach (var p in Directory.EnumerateFileSystemEntries(dir))
        {
            var n = Path.GetFileName(p);
            if (string.Equals(n, MarkerName, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Junction.GetTarget(p);
            if (target != null)
            {
                // Only a link into home's folder is clauderoo's to remove.
                if (!SamePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target)), source)) return false;
                mine.Add(p);
            }
            else if (File.Exists(p) && m.Copies.TryGetValue(n, out var h) && Hash(p) == h) mine.Add(p);
            else return false;
        }
        foreach (var p in mine)
        {
            if (Junction.GetTarget(p) != null) Directory.Delete(p); // non-recursive
            else File.Delete(p);
        }
        File.Delete(Path.Combine(dir, MarkerName));
        Directory.Delete(dir); // non-recursive: fails rather than delete anything left
        return true;
    }

    /// <summary>
    /// Makes a filtered folder the profile's own (cr own): each link becomes a copy of what it pointed to and the
    /// marker goes, so nothing in the folder leads into home any more.
    /// </summary>
    public static void MakeOwned(string dir, Action<string, string> copyDir)
    {
        var m = Read(dir);
        foreach (var n in m.Links)
        {
            var p = Path.Combine(dir, n);
            var target = Junction.GetTarget(p);
            if (target == null) continue;
            var tmp = p + ".tmp-copy";
            if (Directory.Exists(tmp)) throw new ClauderooException($"{tmp} is in the way; move it and try again.");
            if (Directory.Exists(target)) copyDir(target, tmp);
            else Directory.CreateDirectory(tmp);
            Directory.Delete(p); // non-recursive: removes only the junction
            Directory.Move(tmp, p);
        }
        File.Delete(Path.Combine(dir, MarkerName));
    }

    private static void RemoveCopy(string profile, string item, string dir, string n, string hash, string stamp, SyncReport report)
    {
        var p = Path.Combine(dir, n);
        if (!File.Exists(p)) return;
        if (Hash(p) == hash) { File.Delete(p); return; } // exactly what clauderoo copied from home
        var backup = $"{n}.bak-{stamp}";
        File.Move(p, Path.Combine(dir, backup), overwrite: true);
        report.Notices.Add($"{profile}: {item}\\{n} is no longer shared; your changed copy was saved as {item}\\{backup}.");
    }

    private static void Copy(string source, string target)
    {
        var tmp = AtomicFile.TmpName(target);
        try
        {
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}
