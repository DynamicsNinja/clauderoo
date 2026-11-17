namespace Clauderoo.Core;

public static class FolderMap
{
    /// <summary>Full path with no trailing separator, except for a drive or UNC root, which keeps it.</summary>
    public static string Normalize(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)) return full;
        var trimmed = full.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary>Longest mapped prefix of <paramref name="dir"/>, compared case-insensitively on segment boundaries.</summary>
    public static (string Profile, string MappedDir)? Resolve(ClauderooConfig cfg, string dir)
    {
        var d = Normalize(dir);
        (string Profile, string MappedDir)? best = null;
        if (cfg.Folders is null) return null;
        foreach (var (key, profile) in cfg.Folders)
        {
            var k = Normalize(key);
            var prefix = k.EndsWith('\\') ? k : k + "\\";
            var hit = d.Equals(k, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (hit && (best is null || k.Length > best.Value.MappedDir.Length)) best = (profile, k);
        }
        return best;
    }
}
