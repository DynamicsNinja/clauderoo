namespace Clauderoo.Core;

public class SyncReport
{
    public List<string> Notices { get; } = [];
    public List<string> Problems { get; } = [];
}

public class Syncer(ClauderooConfig cfg, ClauderooPaths paths, Func<DateTime> now)
{
    // 'now' is used by file sync (backup names) added later.
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
            if (!Directory.Exists(link) && !File.Exists(link))
            {
                Junction.Create(link, source);
                continue;
            }

            var target = Junction.GetTarget(link);
            if (target == null)
                report.Problems.Add($"{profile}: {d} is a real folder, not shared (run 'clauderoo share {profile} {d}' to share it)");
            else if (!string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(source), StringComparison.OrdinalIgnoreCase))
                report.Problems.Add($"{profile}: {d} is linked to {target}, not to {source}");
        }
    }
}
