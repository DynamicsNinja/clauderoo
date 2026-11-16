using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clauderoo.Core;

public class SyncReport
{
    public List<string> Notices { get; } = [];
    public List<string> Problems { get; } = [];
}

public class Syncer(ClauderooConfig cfg, ClauderooPaths paths, Func<DateTime> now)
{
    private readonly Func<DateTime> _now = now;

    public SyncReport Sync(string profile)
    {
        var report = new SyncReport();
        if (!cfg.Profiles.TryGetValue(profile, out var entry))
            throw new ClauderooException($"No such profile: {profile}");
        if (entry.Home) return report;

        var dir = paths.ProfileDir(profile);
        Directory.CreateDirectory(dir);
        SyncDirs(profile, entry, dir, report);
        SyncState state;
        try { state = SyncState.Load(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Problems.Add($"{profile}: could not read sync state: {e.Message}");
            return report;
        }
        SyncFiles(profile, entry, dir, state, report);
        try { SyncMcp(profile, entry, dir, state, report); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Problems.Add($"{profile}: could not sync mcpServers: {e.Message}");
        }
        try { state.Save(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Problems.Add($"{profile}: could not save sync state: {e.Message}");
        }
        return report;
    }

    public SyncReport SyncAll()
    {
        var all = new SyncReport();
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (entry.Home) continue;
            try
            {
                var r = Sync(name);
                all.Notices.AddRange(r.Notices);
                all.Problems.AddRange(r.Problems);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
            {
                all.Problems.Add($"{name}: could not sync: {e.Message}");
            }
        }
        return all;
    }

    private void SyncDirs(string profile, ProfileEntry entry, string dir, SyncReport report)
    {
        foreach (var d in SharedItems.Dirs)
        {
            if (entry.Overrides.Contains(d)) continue;
            var source = Path.Combine(cfg.HomeDir, d);
            if (!Directory.Exists(source)) continue;

            var link = Path.Combine(dir, d);
            var target = Junction.GetTarget(link);
            if (target == null)
            {
                if (File.Exists(link))
                    report.Problems.Add($"{profile}: {d} is a file, not a shared folder");
                else if (Directory.Exists(link))
                    report.Problems.Add($"{profile}: {d} is a real folder, not shared (run 'clauderoo share {profile} {d}' to share it)");
                else
                {
                    try { Junction.Create(link, source); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
                    {
                        report.Problems.Add($"{profile}: could not sync {d}: {e.Message}");
                    }
                }
                continue;
            }
            if (!string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(source), StringComparison.OrdinalIgnoreCase))
                report.Problems.Add($"{profile}: {d} is linked to {target}, not to {source}");
        }
    }

    private static void CopyAtomic(string source, string target)
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

    private string Stamp => _now().ToString("yyyyMMdd-HHmmss");

    private static string HashBytes(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private void SyncFiles(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        foreach (var f in SharedItems.Files)
        {
            if (entry.Overrides.Contains(f)) continue;
            var source = Path.Combine(cfg.HomeDir, f);
            if (!File.Exists(source)) continue;
            try
            {
            var target = Path.Combine(dir, f);
            var srcHash = HashFile(source);

            if (File.Exists(target))
            {
                var tgtHash = HashFile(target);
                state.Hashes.TryGetValue(f, out var recorded);
                if (tgtHash != recorded && tgtHash != srcHash)
                {
                    var backup = $"{f}.bak-{Stamp}";
                    File.Copy(target, Path.Combine(dir, backup), overwrite: true);
                    report.Notices.Add(Notice(profile, f, backup));
                }
                if (tgtHash != srcHash) CopyAtomic(source, target);
            }
            else
            {
                CopyAtomic(source, target);
            }
            state.Hashes[f] = srcHash;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Problems.Add($"{profile}: could not sync {f}: {e.Message}");
            }
        }
    }

    private string Notice(string profile, string item, string backup) =>
        $"{profile}: your changes to {item} were replaced by {cfg.HomeProfileName}'s (saved {backup}). Use 'clauderoo own {profile} {item}' to keep {profile}'s own.";

    private static string McpHash(JsonNode? node) => HashBytes(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"));

    /// <summary>Test hook: runs just before the profile .claude.json is replaced.</summary>
    public Action? BeforeMcpCommit { get; set; }

    private static (DateTime, long)? Signature(string file) =>
        File.Exists(file) ? (File.GetLastWriteTimeUtc(file), new FileInfo(file).Length) : null;

    private JsonNode? ReadHomeMcp(string profile, SyncReport report, out bool ok)
    {
        ok = true;
        if (!File.Exists(cfg.HomeJson)) return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(cfg.HomeJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return (JsonNode.Parse(fs) as JsonObject)?[SharedItems.McpServers];
            }
            catch (JsonException)
            {
                // Claude may be mid-write: wait once and retry before reporting.
                if (attempt == 0) { Thread.Sleep(200); continue; }
                report.Problems.Add($"{profile}: home .claude.json is not valid JSON; MCP servers not synced");
                ok = false;
                return null;
            }
        }
    }

    private void SyncMcp(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        const string key = SharedItems.McpServers;
        if (entry.Overrides.Contains(key)) return;

        var homeMcp = ReadHomeMcp(profile, report, out var ok);
        if (!ok) return;
        var homeHash = McpHash(homeMcp);

        var file = Path.Combine(dir, ".claude.json");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sig = Signature(file);
            JsonObject root;
            if (sig != null)
            {
                try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new JsonException(); }
                catch (JsonException)
                {
                    report.Problems.Add($"{profile}: .claude.json is not valid JSON; MCP servers not synced");
                    return;
                }
            }
            else root = new JsonObject();

            var current = root[key];
            var curHash = McpHash(current);
            state.Hashes.TryGetValue(key, out var recorded);
            var hasUserValue = current != null && !(current is JsonObject { Count: 0 });
            string? notice = null;
            if (hasUserValue && curHash != recorded && curHash != homeHash && sig != null)
            {
                var backup = $"mcpServers.bak-{Stamp}.json";
                File.WriteAllText(Path.Combine(dir, backup), current?.ToJsonString() ?? "null");
                notice = Notice(profile, key, backup);
            }

            if (curHash != homeHash || sig == null)
            {
                if (homeMcp == null) root.Remove(key);
                else root[key] = homeMcp.DeepClone();
                var tmp = AtomicFile.TmpName(file);
                try
                {
                    File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    BeforeMcpCommit?.Invoke();
                    if (Signature(file) != sig)
                    {
                        File.Delete(tmp);
                        continue; // Claude wrote the file meanwhile: re-read and redo the merge
                    }
                    File.Move(tmp, file, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    throw;
                }
            }
            if (notice != null) report.Notices.Add(notice);
            state.Hashes[key] = homeHash;
            return;
        }
        report.Problems.Add($"{profile}: .claude.json kept changing; MCP servers not synced");
    }
}
