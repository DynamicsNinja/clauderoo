namespace Clauderoo.Core;

public partial class ProfileManager
{
    /// <summary>
    /// Sets the default profile. Null or the home profile clears the User-scope
    /// CLAUDE_CONFIG_DIR; any other profile is synced and the variable points at its folder.
    /// </summary>
    public SyncReport SetDefault(string? name)
    {
        var cfg = Config;
        string target;
        if (name == null)
        {
            target = cfg.Profiles.FirstOrDefault(kv => kv.Value.Home).Key
                     ?? throw new ClauderooException("No home profile is configured.");
        }
        else
        {
            Entry(cfg, name); // throws for unknown
            target = name;
        }

        var report = new SyncReport();
        if (cfg.Profiles[target].Home)
        {
            env.SetUser(EnvVar, null);
        }
        else
        {
            report = NewSyncer(cfg).Sync(target);
            env.SetUser(EnvVar, paths.ProfileDir(target));
        }
        cfg.DefaultProfile = target;
        ConfigStore.Save(paths, cfg);
        return report;
    }
}
