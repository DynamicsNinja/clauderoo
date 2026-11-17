using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

/// <summary>
/// One Claude conversation file, <c>&lt;configDir&gt;\projects\&lt;Slug&gt;\&lt;Id&gt;.jsonl</c>. FilePath is for
/// clauderoo's own use and is never printed or put in JSON; FirstPrompt is the user's own text, shown locally only.
/// </summary>
public record Conversation(string Id, string Profile, bool Isolated, string Slug, string? Cwd, DateTimeOffset When,
    string? FirstPrompt, string FilePath);

public enum CopyOutcome { Copied, AlreadyUpToDate, BackedUpAndCopied }

/// <summary>
/// Lists conversations across profiles (home read-only) and copies one into another profile so it can be resumed
/// there. Claude's jsonl format is internal, so parsing is defensive: anything that doesn't parse is skipped.
/// </summary>
public static partial class ConversationIndex
{
    public const int HeadBytes = 64 * 1024;
    public const int PromptLength = 80;

    public const string HomeTargetRefused =
        "Conversations can't be copied into the home profile, because clauderoo never writes inside ~/.claude.";

    public static IReadOnlyList<Conversation> List(ProfileManager pm, string? profile = null)
    {
        var cfg = pm.Config;
        if (profile != null) pm.Get(profile); // unknown profile -> ClauderooException
        var result = new List<Conversation>();
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (profile != null && name != profile) continue;
            var main = entry.Home ? cfg.HomeDir : pm.Paths.ProfileDir(name);
            Scan(main, name, isolated: false, result);
            Scan(pm.Paths.IsolatedDir(name), name, isolated: true, result);
        }
        return result
            .OrderByDescending(c => c.When)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Profile, StringComparer.Ordinal)
            .ToList();
    }

    private static void Scan(string configDir, string profile, bool isolated, List<Conversation> into)
    {
        var projects = Path.Combine(configDir, "projects");
        string[] slugDirs;
        try { slugDirs = Directory.Exists(projects) ? Directory.GetDirectories(projects) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var slugDir in slugDirs)
        {
            string[] files;
            try { files = Directory.GetFiles(slugDir, "*.jsonl"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                try { into.Add(Read(file, profile, isolated, Path.GetFileName(slugDir))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* gone or locked: skip */ }
            }
        }
    }

    private static Conversation Read(string file, string profile, bool isolated, string slug)
    {
        var when = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        string? id = null, cwd = null, prompt = null;
        // Claude may be writing this file right now: read-only, sharing everything, and only the head.
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var buf = new byte[HeadBytes];
            var n = 0;
            int r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            var text = new UTF8Encoding(false, false).GetString(buf, 0, n);
            foreach (var line in text.Split('\n'))
            {
                if (id != null && cwd != null && prompt != null) break;
                var l = line.Trim();
                if (l.Length == 0 || l[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(l);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    id ??= Str(root, "sessionId") is { } s && SafeId(s) ? s : null;
                    cwd ??= Str(root, "cwd") is { Length: > 0 } c ? c : null;
                    if (prompt == null && Str(root, "type") == "user") prompt = UserText(root);
                }
                catch (JsonException) { /* partial or foreign line */ }
            }
        }
        var fromName = Path.GetFileNameWithoutExtension(file);
        return new Conversation(id ?? fromName, profile, isolated, slug, cwd, when, prompt, file);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The text of a user message, or null for a tool_result (or anything without text).
    private static string? UserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        string? raw = null;
        if (content.ValueKind == JsonValueKind.String) raw = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                var type = Str(part, "type");
                if (type == "tool_result") return null;
                if (type == "text" && Str(part, "text") is { } t) sb.Append(t).Append(' ');
            }
            raw = sb.ToString();
        }
        if (raw == null) return null;
        var collapsed = Whitespace().Replace(raw, " ").Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length > PromptLength ? collapsed[..PromptLength] : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Ids end up in paths and on Claude's command line, so only plain ones are accepted.
    private static bool SafeId(string s) =>
        s.Length is > 0 and <= 128 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static bool SafeSegment(string s) =>
        s.Length > 0 && s != "." && s != ".." && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>The single conversation whose id starts with <paramref name="idPrefix"/> (case-insensitive). The same
    /// conversation found in several profiles counts once, and its newest copy is returned.</summary>
    public static Conversation Find(IReadOnlyList<Conversation> all, string idPrefix)
    {
        if (string.IsNullOrWhiteSpace(idPrefix))
            throw new ClauderooException("Give the start of a conversation id. Run 'cr sessions' to list them.");
        var matches = all.Where(c => c.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = matches.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
            throw new ClauderooException($"No conversation id starts with '{idPrefix}'. Run 'cr sessions' to list them.");
        if (ids.Count > 1)
            throw new ClauderooException(
                $"'{idPrefix}' matches {ids.Count} conversations ({string.Join(", ", ids.Take(5).Select(Short))}" +
                $"{(ids.Count > 5 ? ", …" : "")}); give more of the id.");
        return matches.OrderByDescending(c => c.When).First();
    }

    public static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>
    /// Copies the conversation file (and its <c>&lt;id&gt;\</c> folder, if any) into the target profile's
    /// <c>projects\&lt;slug&gt;\</c>. A target copy that is newer or at least as big is kept; an older one is moved
    /// aside as <c>&lt;id&gt;.jsonl.bak-&lt;stamp&gt;</c>. Never writes into the home profile or follows a junction.
    /// </summary>
    public static CopyOutcome CopyTo(ProfileManager pm, Conversation c, string targetProfile, bool targetIsolated)
    {
        var cfg = pm.Config;
        var entry = pm.Get(targetProfile);
        if (entry.Home) throw new ClauderooException(HomeTargetRefused);
        if (!SafeSegment(c.Slug)) throw new ClauderooException($"Unexpected conversation folder '{c.Slug}'.");
        var fileName = Path.GetFileName(c.FilePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!SafeSegment(fileName) || !SafeId(stem))
            throw new ClauderooException($"Unexpected conversation file '{fileName}'.");

        var root = targetIsolated ? pm.Paths.IsolatedDir(targetProfile) : pm.Paths.ProfileDir(targetProfile);
        var projects = Path.Combine(root, "projects");
        var slugDir = Path.Combine(projects, c.Slug);
        var target = Path.Combine(slugDir, fileName);
        if (SamePath(target, c.FilePath)) return CopyOutcome.AlreadyUpToDate;

        // A junction anywhere on the way could lead into ~/.claude, so the target path must be plain folders.
        foreach (var p in new[] { root, projects, slugDir })
            if (IsReparsePoint(p))
                throw new ClauderooException($"{p} is a link; clauderoo won't copy a conversation through it.");
        if (IsUnder(Path.GetFullPath(target), cfg.HomeDir)) throw new ClauderooException(HomeTargetRefused);

        var src = new FileInfo(c.FilePath);
        if (!src.Exists) throw new ClauderooException($"Conversation {Short(c.Id)} is no longer there; run 'cr sessions' again.");
        var existing = new FileInfo(target);
        if (existing.Exists && (existing.LastWriteTimeUtc >= src.LastWriteTimeUtc || existing.Length >= src.Length))
            return CopyOutcome.AlreadyUpToDate;

        Directory.CreateDirectory(slugDir);
        var stamp = pm.Stamp;
        var tmpFile = Path.Combine(slugDir, $".{fileName}.tmp-{Guid.NewGuid():N}");
        var srcFolder = Path.Combine(Path.GetDirectoryName(c.FilePath)!, stem);
        var tgtFolder = Path.Combine(slugDir, stem);
        string? tmpFolder = null;
        try
        {
            CopyFile(c.FilePath, tmpFile, src.LastWriteTimeUtc);
            if (Directory.Exists(srcFolder) && !IsReparsePoint(srcFolder))
            {
                tmpFolder = Path.Combine(slugDir, $".{stem}.tmp-{Guid.NewGuid():N}");
                CopyTree(srcFolder, tmpFolder);
            }

            // Everything is staged; now swap it in, moving older copies aside (never deleting them).
            if (tmpFolder != null)
            {
                if (Directory.Exists(tgtFolder) || File.Exists(tgtFolder))
                    Directory.Move(tgtFolder, $"{tgtFolder}.bak-{stamp}");
                Directory.Move(tmpFolder, tgtFolder);
                tmpFolder = null;
            }
            if (existing.Exists) File.Move(target, $"{target}.bak-{stamp}");
            File.Move(tmpFile, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tmpFile);
            if (tmpFolder != null) TryDeleteOwnTree(tmpFolder);
            throw new ClauderooException(
                $"Couldn't copy conversation {Short(c.Id)} to {targetProfile}: {e.Message} " +
                "If that conversation is still open in Claude, close it and try again.");
        }
        return existing.Exists ? CopyOutcome.BackedUpAndCopied : CopyOutcome.Copied;
    }

    private static void CopyFile(string source, string dest, DateTime lastWriteUtc)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        // Keeps the source's time, so the next copy can tell whether the source moved on.
        File.SetLastWriteTimeUtc(dest, lastWriteUtc);
    }

    // Recursive copy that skips every reparse point (junctions, symlinks), so it never leaves the source folder.
    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in new DirectoryInfo(source).EnumerateFiles())
        {
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyFile(f.FullName, Path.Combine(dest, f.Name), f.LastWriteTimeUtc);
        }
        foreach (var d in new DirectoryInfo(source).EnumerateDirectories())
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyTree(d.FullName, Path.Combine(dest, d.Name));
        }
    }

    // Only ever called on a temp tree this class made with CopyTree, which never contains reparse points.
    private static void TryDeleteOwnTree(string dir)
    {
        try { if (Directory.Exists(dir) && !IsReparsePoint(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    private static bool SamePath(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string dir)
    {
        var d = Norm(dir);
        var p = Norm(path);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
