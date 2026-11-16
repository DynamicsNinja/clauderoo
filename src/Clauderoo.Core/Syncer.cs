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
        var state = SyncState.Load(dir);
        SyncFiles(profile, entry, dir, state, report);
        SyncMcp(profile, entry, dir, state, report);
        state.Save(dir);
        return report;
    }

    public SyncReport SyncAll()
    {
        var all = new SyncReport();
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (entry.Home) continue;
            var r = Sync(name);
            all.Notices.AddRange(r.Notices);
            all.Problems.AddRange(r.Problems);
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
                    Junction.Create(link, source);
                continue;
            }
            if (!string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(source), StringComparison.OrdinalIgnoreCase))
                report.Problems.Add($"{profile}: {d} is linked to {target}, not to {source}");
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
                if (tgtHash != srcHash) File.Copy(source, target, overwrite: true);
            }
            else
            {
                File.Copy(source, target);
            }
            state.Hashes[f] = srcHash;
        }
    }

    private string Notice(string profile, string item, string backup) =>
        $"{profile}: your changes to {item} were replaced by {cfg.HomeProfileName}'s (saved {backup}). Use 'clauderoo own {profile} {item}' to keep {profile}'s own.";

    private static string McpHash(JsonNode? node) => HashBytes(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null"));

    private void SyncMcp(string profile, ProfileEntry entry, string dir, SyncState state, SyncReport report)
    {
        const string key = SharedItems.McpServers;
        if (entry.Overrides.Contains(key)) return;

        JsonNode? homeMcp = null;
        if (File.Exists(cfg.HomeJson))
        {
            try
            {
                using var fs = new FileStream(cfg.HomeJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                homeMcp = (JsonNode.Parse(fs) as JsonObject)?[key];
            }
            catch (JsonException)
            {
                report.Problems.Add($"{profile}: home .claude.json is not valid JSON; MCP servers not synced");
                return;
            }
        }
        var homeHash = McpHash(homeMcp);

        var file = Path.Combine(dir, ".claude.json");
        JsonObject root;
        if (File.Exists(file))
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
        if (curHash != recorded && curHash != homeHash && File.Exists(file))
        {
            var backup = $"mcpServers.bak-{Stamp}.json";
            File.WriteAllText(Path.Combine(dir, backup), current?.ToJsonString() ?? "null");
            report.Notices.Add(Notice(profile, key, backup));
        }

        if (curHash != homeHash || !File.Exists(file))
        {
            if (homeMcp == null) root.Remove(key);
            else root[key] = homeMcp.DeepClone();
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        state.Hashes[key] = homeHash;
    }
}
