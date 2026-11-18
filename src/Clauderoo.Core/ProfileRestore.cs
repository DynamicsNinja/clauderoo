namespace Clauderoo.Core;

public partial class ProfileManager
{
    private SessionTracker? _sessions;

    /// <summary>Claude sessions clauderoo started (see <see cref="SessionTracker"/>).</summary>
    public SessionTracker Sessions
    {
        get => _sessions ??= new SessionTracker(paths);
        internal set => _sessions = value;
    }

    private UsageCache? _usage;

    /// <summary>The cached usage readings (see <see cref="UsageCache"/>), kept in step with renames and removals.</summary>
    public UsageCache Usage
    {
        get => _usage ??= new UsageCache(paths);
        internal set => _usage = value;
    }

    private AutoSwitchStore? _autoSwitchState;

    /// <summary>The auto-switch state file, kept in step with renames and removals.</summary>
    public AutoSwitchStore AutoSwitchState => _autoSwitchState ??= new AutoSwitchStore(paths);

    /// <summary>Syncs one profile with the home profile.</summary>
    public SyncReport Sync(string name)
    {
        var cfg = Config;
        Entry(cfg, name);
        return NewSyncer(cfg).Sync(name);
    }

    private void RefuseIfRunning(string name, bool force)
    {
        if (force) return;
        var n = Sessions.Running(name).Count;
        if (n > 0) throw new SessionsRunningException(name, n);
    }

    /// <summary>Applies the fix a <see cref="Problem"/> offers and returns the profile's new sync report.</summary>
    public SyncReport Fix(Problem p)
    {
        switch (p.Fix)
        {
            case ProblemFix.ShareAgain:
            {
                var item = ItemFor(p);
                if (!SharedItems.IsKnown(Config, item)) throw new ClauderooException("Couldn't tell which folder this problem is about.");
                return Share(p.Profile, item);
            }
            case ProblemFix.RecreateLink:
            {
                var cfg = Config;
                Entry(cfg, p.Profile);
                var item = ItemFor(p);
                if (!SharedItems.IsDir(cfg, item)) throw new ClauderooException("Couldn't tell which folder this problem is about.");
                var link = Path.Combine(paths.ProfileDir(p.Profile), item);
                // Non-recursive: removes only the junction, never what it points to.
                if (Junction.GetTarget(link) != null) Directory.Delete(link);
                return NewSyncer(cfg).Sync(p.Profile);
            }
            case ProblemFix.Resync:
                return Sync(p.Profile);
            case ProblemFix.KeepOwn:
            {
                // The same as `clauderoo own`: home's shared entries are copied in; nothing is deleted.
                var cfg = Config;
                Entry(cfg, p.Profile);
                var item = ItemFor(p);
                if (!SharedItems.IsDir(cfg, item)) throw new ClauderooException("Couldn't tell which folder this problem is about.");
                Own(p.Profile, item);
                return Sync(p.Profile);
            }
            default:
                throw new ClauderooException("There is nothing clauderoo can fix automatically for this problem.");
        }
    }

    private static string ItemFor(Problem p) =>
        ProblemTranslator.ItemOf(p, p.Profile) ?? throw new ClauderooException("Couldn't tell which folder this problem is about.");

    /// <summary>Restores the newest removed copy of <paramref name="name"/>; see <see cref="Restore(string, string?)"/>.</summary>
    public SyncReport Restore(string name) => Restore(name, at: null);

    /// <summary>
    /// Moves a removed copy of <paramref name="name"/> back and re-adds it: the one removed at <paramref name="at"/>
    /// (an id from <see cref="ListRemoved"/>), or the newest. Its isolated folder comes back too. Never overwrites:
    /// refuses when a profile (or folder) with that name exists. The settings Remove saved (provider included) come
    /// back; an API key was deleted by Remove and must be entered again (a notice says so).
    /// </summary>
    public SyncReport Restore(string name, string? at)
    {
        ProfileName.Validate(name);
        var cfg = Config;
        var dir = paths.ProfileDir(name);
        if (cfg.Profiles.ContainsKey(name) || Directory.Exists(dir))
            throw new ClauderooException($"A profile named '{name}' already exists.");

        var copy = FindRemoved(name, at);
        if (copy.ProfileDir is not { } source)
            throw new ClauderooException(
                $"The copy of '{name}' removed at {copy.Id} has only its isolated folder, so it can't be restored as a profile.");

        try { Directory.Move(source, dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ClauderooException($"Couldn't move {name}'s folder back: {e.Message}");
        }

        // Remove saved the profile's settings (never the API key) next to its folder.
        var entryFile = Path.Combine(dir, EntryFileName);
        var entry = ConfigStore.ReadEntry(entryFile) ?? new ProfileEntry();
        entry.Home = false;
        // Remove unlinked the shared-folder junctions; a real folder left behind was the profile's own copy.
        foreach (var d in SharedItems.DirsOf(cfg))
        {
            var sub = Path.Combine(dir, d);
            if (Directory.Exists(sub) && Junction.GetTarget(sub) == null && !FilteredDir.IsFiltered(sub) && !entry.Overrides.Contains(d))
                entry.Overrides.Add(d);
        }
        try
        {
            cfg = ConfigStore.Update(paths, c =>
            {
                if (c.Profiles.ContainsKey(name)) throw new ClauderooException($"A profile named '{name}' already exists.");
                c.Profiles[name] = entry;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
        {
            try { Directory.Move(dir, source); } catch (Exception) { /* leave it; the message says where it is */ }
            throw new ClauderooException($"Couldn't restore {name}: {e.Message}");
        }
        try { File.Delete(entryFile); } catch (Exception) { }

        var report = NewSyncer(cfg).Sync(name);
        // Its isolated folder (a separate login) came aside with it, so it comes back with it.
        var isoDir = paths.IsolatedDir(name);
        if (copy.IsolatedDir != null && !Directory.Exists(isoDir))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(isoDir)!);
                Directory.Move(copy.IsolatedDir, isoDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Notices.Add($"{name}'s isolated folder stayed at {copy.IsolatedDir}: {e.Message}");
            }
        }
        var older = ListRemoved(name).Count(c => c.CanRestore);
        if (older > 0)
            report.Notices.Add($"{older} other removed {(older == 1 ? "copy" : "copies")} of {name} remain{(older == 1 ? "s" : "")}. " +
                               "See them with 'clauderoo removed'.");
        if (Providers.Of(entry) == Providers.ApiKey)
            report.Notices.Add($"{name} is back. Its API key was deleted when it was removed — enter it again in Manage profiles.");
        if (Providers.Of(entry) == Providers.Token)
            report.Notices.Add($"{name} is back. Its Claude token was deleted when it was removed — set it again in Manage profiles or with 'cr provider {name} token'.");
        return report;
    }

    internal const string EntryFileName = ".clauderoo-entry.json";
}
