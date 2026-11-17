using System.Text.RegularExpressions;

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
                if (!SharedItems.IsKnown(item)) throw new ClauderooException("Couldn't tell which folder this problem is about.");
                return Share(p.Profile, item);
            }
            case ProblemFix.RecreateLink:
            {
                var cfg = Config;
                Entry(cfg, p.Profile);
                var item = ItemFor(p);
                if (!SharedItems.IsDir(item)) throw new ClauderooException("Couldn't tell which folder this problem is about.");
                var link = Path.Combine(paths.ProfileDir(p.Profile), item);
                // Non-recursive: removes only the junction, never what it points to.
                if (Junction.GetTarget(link) != null) Directory.Delete(link);
                return NewSyncer(cfg).Sync(p.Profile);
            }
            case ProblemFix.Resync:
                return Sync(p.Profile);
            default:
                throw new ClauderooException("There is nothing clauderoo can fix automatically for this problem.");
        }
    }

    private static string ItemFor(Problem p) =>
        ProblemTranslator.ItemOf(p, p.Profile) ?? throw new ClauderooException("Couldn't tell which folder this problem is about.");

    /// <summary>
    /// Moves the newest removed copy of <paramref name="name"/> back and re-adds it. Never overwrites: refuses when a
    /// profile (or folder) with that name exists. The settings Remove saved (provider included) come back; an API key
    /// was deleted by Remove and must be entered again (a notice says so).
    /// </summary>
    public SyncReport Restore(string name)
    {
        ProfileName.Validate(name);
        var cfg = Config;
        var dir = paths.ProfileDir(name);
        if (cfg.Profiles.ContainsKey(name) || Directory.Exists(dir))
            throw new ClauderooException($"A profile named '{name}' already exists.");

        var pattern = new Regex($@"^{Regex.Escape(name)}-\d{{8}}-\d{{6}}$", RegexOptions.IgnoreCase);
        var source = Directory.Exists(paths.RemovedDir)
            ? Directory.GetDirectories(paths.RemovedDir)
                .Where(d => pattern.IsMatch(Path.GetFileName(d)))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        if (source == null)
            throw new ClauderooException($"No removed profile named '{name}' was found.");

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
        foreach (var d in SharedItems.Dirs)
        {
            var sub = Path.Combine(dir, d);
            if (Directory.Exists(sub) && Junction.GetTarget(sub) == null && !entry.Overrides.Contains(d))
                entry.Overrides.Add(d);
        }
        cfg.Profiles[name] = entry;
        try { ConfigStore.Save(paths, cfg); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { Directory.Move(dir, source); } catch (Exception) { /* leave it; the message says where it is */ }
            throw new ClauderooException($"Couldn't restore {name}: {e.Message}");
        }
        try { File.Delete(entryFile); } catch (Exception) { }

        var report = NewSyncer(cfg).Sync(name);
        if (Providers.Of(entry) == Providers.ApiKey)
            report.Notices.Add($"{name} is back. Its API key was deleted when it was removed — enter it again in Manage profiles.");
        return report;
    }

    internal const string EntryFileName = ".clauderoo-entry.json";
}
