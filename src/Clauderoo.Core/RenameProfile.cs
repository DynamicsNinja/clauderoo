namespace Clauderoo.Core;

public partial class ProfileManager
{
    /// <summary>Test hook: runs after the folder move, before the config is saved.</summary>
    internal Action? BeforeRenameCommit { get; set; }

    /// <summary>Test hook: runs after the config is saved and the env var updated, before the re-sync.</summary>
    internal Action? AfterRenameCommit { get; set; }

    /// <summary>
    /// Renames a profile in place: folder, config key, default pointer, User CLAUDE_CONFIG_DIR and
    /// apiKeyHelper. All validation happens before any change; a failure after the folder move rolls back.
    /// The home profile only gets a new label - its folder is never touched.
    /// </summary>
    public SyncReport Rename(string oldName, string newName, bool force = false)
    {
        var cfg = Config;
        var entry = Entry(cfg, oldName);
        ProfileName.Validate(newName);
        if (oldName == newName) return new SyncReport();
        RefuseIfRunning(oldName, force);
        var oldDir = paths.ProfileDir(oldName);
        var newDir = paths.ProfileDir(newName);
        if (cfg.Profiles.ContainsKey(newName) || (!entry.Home && Directory.Exists(newDir)))
            throw new ClauderooException($"A profile named '{newName}' already exists.");
        if (!entry.Home)
            RefuseIfPluginRecordsReference(cfg, oldName, oldDir, force, $"rename {oldName} {newName}");

        var original = Config; // untouched copy for rollback
        var oldEnv = env.GetUser(EnvVar);

        var moved = false;
        var envChanged = false;
        if (!entry.Home && Directory.Exists(oldDir))
        {
            try { Directory.Move(oldDir, newDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ClauderooException(
                    $"Couldn't rename {oldName}'s folder — close {oldName}'s Claude sessions and try again.");
            }
            moved = true;
        }

        try
        {
            BeforeRenameCommit?.Invoke();

            // Rebuild the map so the entry keeps its position.
            var profiles = new Dictionary<string, ProfileEntry>();
            foreach (var kv in cfg.Profiles) profiles[kv.Key == oldName ? newName : kv.Key] = kv.Value;
            cfg.Profiles = profiles;
            if (cfg.DefaultProfile == oldName) cfg.DefaultProfile = newName;
            if (cfg.AutoSwitch is { } auto)
                auto.Order = auto.Order.Select(n => n == oldName ? newName : n).ToList();
            ConfigStore.Save(paths, cfg);

            if (!entry.Home && !string.IsNullOrEmpty(oldEnv) && Norm(oldEnv) == Norm(oldDir))
            {
                env.SetUser(EnvVar, newDir);
                envChanged = true;
            }

            AfterRenameCommit?.Invoke();

            try { Sessions.Rename(oldName, newName); } catch (IOException) { /* tracking is best effort */ }
            try { Usage.Rename(oldName, newName); } catch (IOException) { /* the cache is best effort */ }

            return entry.Home ? new SyncReport() : NewSyncer(cfg).Sync(newName);
        }
        catch
        {
            // Best-effort rollback; the original exception is what the caller needs to see.
            try { if (moved) Directory.Move(newDir, oldDir); } catch { }
            try { ConfigStore.Save(paths, original); } catch { }
            try { if (envChanged) env.SetUser(EnvVar, oldEnv); } catch { }
            try { if (moved) NewSyncer(original).Sync(oldName); } catch { }
            throw;
        }
    }
}
