namespace Clauderoo.Core;

public partial class ProfileManager
{
    public void Map(string profile, string dir)
    {
        var cfg = Config;
        Entry(cfg, profile);
        var key = FolderMap.Normalize(dir);
        foreach (var k in cfg.Folders.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
            cfg.Folders.Remove(k);
        cfg.Folders[key] = profile;
        ConfigStore.Save(paths, cfg);
    }

    public bool Unmap(string dir)
    {
        var cfg = Config;
        var key = FolderMap.Normalize(dir);
        var found = cfg.Folders.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return false;
        foreach (var k in found) cfg.Folders.Remove(k);
        ConfigStore.Save(paths, cfg);
        return true;
    }

    public IReadOnlyList<(string Dir, string Profile)> Maps() =>
        Config.Folders.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
}
