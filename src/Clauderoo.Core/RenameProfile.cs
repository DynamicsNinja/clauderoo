namespace Clauderoo.Core;

public partial class ProfileManager
{
    /// <summary>Test hook: runs after the folder move, before the config is saved.</summary>
    internal Action? BeforeRenameCommit { get; set; }

    /// <summary>Test hook: runs after the config is saved and the env var updated, before the re-sync.</summary>
    internal Action? AfterRenameCommit { get; set; }

    /// <summary>Test hook: runs in Remove after the profile folder moved, before the isolated folder moves.</summary>
    internal Action? BeforeIsolatedMove { get; set; }

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

        var oldIso = paths.IsolatedDir(oldName);
        var newIso = paths.IsolatedDir(newName);
        // Refused even when the old profile has no isolated folder: a stray one would silently become the new profile's.
        if (Directory.Exists(newIso))
            throw new ClauderooException($"An isolated folder for '{newName}' already exists: {newIso}");

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

        var isoMoved = false;
        try
        {
            if (Directory.Exists(oldIso))
            {
                try { Directory.Move(oldIso, newIso); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new ClauderooException(
                        $"Couldn't rename {oldName}'s isolated folder — close {oldName}'s Claude sessions and try again.");
                }
                isoMoved = true;
            }
            BeforeRenameCommit?.Invoke();

            cfg = ConfigStore.Update(paths, c =>
            {
                if (!c.Profiles.ContainsKey(oldName)) throw new ClauderooException($"No such profile: {oldName}");
                if (c.Profiles.ContainsKey(newName)) throw new ClauderooException($"A profile named '{newName}' already exists.");
                RenameIn(c, oldName, newName);
            });
            AppLog.Info($"Profile {oldName} renamed to {newName}.");

            if (!entry.Home && !string.IsNullOrEmpty(oldEnv) && Norm(oldEnv) == Norm(oldDir))
            {
                env.SetUser(EnvVar, newDir);
                envChanged = true;
            }

            AfterRenameCommit?.Invoke();

            try { Sessions.Rename(oldName, newName); } catch (IOException) { /* tracking is best effort */ }
            try { Usage.Rename(oldName, newName); } catch (IOException) { /* the cache is best effort */ }
            try { AutoSwitchState.Rename(oldName, newName); } catch (IOException) { /* state is best effort */ }

            return entry.Home ? new SyncReport() : NewSyncer(cfg).Sync(newName);
        }
        catch
        {
            // Best-effort rollback; the original exception is what the caller needs to see.
            try { if (isoMoved) Directory.Move(newIso, oldIso); } catch { }
            try { if (moved) Directory.Move(newDir, oldDir); } catch { }
            // Undo only our own rename, keeping whatever another process saved meanwhile.
            try
            {
                ConfigStore.UpdateWhen(paths, c =>
                {
                    if (!c.Profiles.ContainsKey(newName) || c.Profiles.ContainsKey(oldName)) return false;
                    RenameIn(c, newName, oldName);
                    return true;
                });
            }
            catch { }
            try { if (envChanged) env.SetUser(EnvVar, oldEnv); } catch { }
            try { if (moved) NewSyncer(original).Sync(oldName); } catch { }
            throw;
        }
    }

    // Renames the entry and every reference to it; rebuilds the map so the entry keeps its position.
    private static void RenameIn(ClauderooConfig cfg, string oldName, string newName)
    {
        var profiles = new Dictionary<string, ProfileEntry>();
        foreach (var kv in cfg.Profiles) profiles[kv.Key == oldName ? newName : kv.Key] = kv.Value;
        cfg.Profiles = profiles;
        if (cfg.DefaultProfile == oldName) cfg.DefaultProfile = newName;
        if (cfg.AutoSwitch is { } auto)
            auto.Order = auto.Order.Select(n => n == oldName ? newName : n).ToList();
        foreach (var k in cfg.Folders.Keys.ToList())
            if (cfg.Folders[k] == oldName) cfg.Folders[k] = newName;
    }
}
