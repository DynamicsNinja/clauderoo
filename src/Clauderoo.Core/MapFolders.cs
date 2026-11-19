namespace Clauderoo.Core;

public partial class ProfileManager
{
    public void Map(string profile, string dir)
    {
        Entry(Config, profile);
        var key = FolderMap.Normalize(dir);
        ConfigStore.Update(paths, cfg =>
        {
            Entry(cfg, profile);
            foreach (var k in cfg.Folders.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                cfg.Folders.Remove(k);
            cfg.Folders[key] = profile;
        });
    }

    public bool Unmap(string dir)
    {
        _ = Config; // not set up: the usual message
        var key = FolderMap.Normalize(dir);
        return ConfigStore.UpdateWhen(paths, cfg =>
        {
            var found = cfg.Folders.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) return false;
            foreach (var k in found) cfg.Folders.Remove(k);
            return true;
        });
    }

    /// <summary>Stops `cr start` offering to map <paramref name="dir"/> (that folder only, not the ones below it).</summary>
    public void SkipMapPrompt(string dir)
    {
        _ = Config;
        var key = FolderMap.Normalize(dir);
        ConfigStore.Update(paths, cfg =>
        {
            cfg.NoMapPrompt ??= [];
            if (!cfg.NoMapPrompt.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))) cfg.NoMapPrompt.Add(key);
        });
    }

    public IReadOnlyList<(string Dir, string Profile)> Maps() =>
        Config.Folders.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
}
