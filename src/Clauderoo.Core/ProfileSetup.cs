namespace Clauderoo.Core;

/// <summary>
/// What <see cref="ProfileManager.Clone"/> did. <see cref="SecretToSet"/> is the source's provider (apikey or token) when
/// the copy starts on the Claude login instead, because secrets are never copied; null otherwise.
/// </summary>
public record CloneResult(SyncReport Report, string? SecretToSet);

/// <summary>What <see cref="ProfileManager.SetHome"/> did: the sync report, and the .claude.json now used.</summary>
public record HomeResult(SyncReport Report, string HomeDir, string HomeJson, bool HomeJsonExists);

public partial class ProfileManager
{
    /// <summary>
    /// Sets (or with null, clears) the folder the tray starts a profile's sessions in. The folder must exist; it is
    /// stored as a full path. Works for the home profile too: only clauderoo's config is written.
    /// </summary>
    public void SetStartDir(string name, string? dir)
    {
        string? full = null;
        if (dir != null)
        {
            try { full = FolderMap.Normalize(dir); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ClauderooException($"Not a valid folder path: {dir}");
            }
            if (!Directory.Exists(full)) throw new ClauderooException($"Folder not found: {full}");
        }
        ConfigStore.Update(paths, c => { Entry(c, name).StartDir = full; });
    }

    /// <summary>
    /// Creates <paramref name="name"/> with <paramref name="source"/>'s settings: provider, Vertex settings, environment
    /// variables, owned items and start folder. Never the login, conversations, API key or token, folder mappings or
    /// any file from either profile's folder: owned items start empty. A source that signs in with an API key or a
    /// token gives a copy on the Claude login (see <see cref="CloneResult.SecretToSet"/>). Cloning the home profile
    /// copies only its start folder.
    /// </summary>
    public CloneResult Clone(string source, string name)
    {
        ProfileName.Validate(name);
        var cfg = Config;
        var src = Entry(cfg, source);
        var dir = paths.ProfileDir(name);
        if (cfg.Profiles.ContainsKey(name) || Directory.Exists(dir))
            throw new ClauderooException($"A profile named '{name}' already exists.");
        // A stray isolated folder holds someone's login: it would silently become the copy's.
        if (Directory.Exists(paths.IsolatedDir(name)))
            throw new ClauderooException($"An isolated folder for '{name}' already exists: {paths.IsolatedDir(name)}");

        var provider = src.Home ? Providers.Login : Providers.Of(src);
        string? secretToSet = provider is Providers.ApiKey or Providers.Token ? provider : null;
        var entry = new ProfileEntry { StartDir = src.StartDir };
        if (!src.Home)
        {
            entry.Provider = provider == Providers.Vertex ? Providers.Vertex : null;
            if (provider == Providers.Vertex && src.Vertex is { } v)
                entry.Vertex = new VertexSettings
                {
                    Project = v.Project, Region = v.Region,
                    SonnetModel = v.SonnetModel, OpusModel = v.OpusModel, HaikuModel = v.HaikuModel,
                };
            entry.Env = new Dictionary<string, string>(src.Env, StringComparer.Ordinal);
            entry.Overrides = src.Overrides.Where(SharedItems.IsKnown).Distinct().ToList();
        }

        Directory.CreateDirectory(dir);
        // An owned folder starts empty, so the sync leaves it alone instead of linking it to home.
        foreach (var item in entry.Overrides.Where(SharedItems.IsDir))
            Directory.CreateDirectory(Path.Combine(dir, item));
        cfg = ConfigStore.Update(paths, c =>
        {
            if (c.Profiles.ContainsKey(name)) throw new ClauderooException($"A profile named '{name}' already exists.");
            c.Profiles[name] = entry;
        });
        return new CloneResult(NewSyncer(cfg).Sync(name), secretToSet);
    }

    private static readonly string[] ClaudeDirMarkers =
        ["settings.json", ".credentials.json", ".claude.json", "CLAUDE.md", "history.jsonl", "projects", "plugins", "todos"];

    /// <summary>True when <paramref name="dir"/> holds something only a Claude config folder has.</summary>
    internal static bool LooksLikeClaudeDir(string dir) =>
        ClaudeDirMarkers.Any(m => File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m)));

    /// <summary>
    /// Points the home profile at <paramref name="dir"/> (when ~/.claude has moved), then re-links every profile's shared
    /// folders that pointed into the old home and re-syncs. The new folder is only read, never written. Its .claude.json
    /// is ~/.claude.json for the standard ~/.claude, otherwise the one inside the folder (as with 'init --home').
    /// </summary>
    public HomeResult SetHome(string dir)
    {
        var cfg = Config;
        string full;
        try { full = FolderMap.Normalize(dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ClauderooException($"Not a valid folder path: {dir}");
        }
        if (!Directory.Exists(full)) throw new ClauderooException($"Folder not found: {full}");
        if (SamePath(full, paths.Root) || IsUnder(full, paths.Root))
            throw new ClauderooException($"{full} is one of clauderoo's own folders, not your Claude folder.");
        if (!LooksLikeClaudeDir(full))
            throw new ClauderooException(
                $"{full} doesn't look like a Claude config folder (it has no settings.json, .credentials.json, projects or plugins).");

        var homeJson = SamePath(full, paths.DefaultHomeDir) ? paths.DefaultHomeJson : Path.Combine(full, ".claude.json");
        string oldHome = "";
        cfg = ConfigStore.Update(paths, c =>
        {
            oldHome = c.HomeDir;
            c.HomeDir = full;
            c.HomeJson = homeJson;
        });

        var report = new SyncReport();
        if (!SamePath(oldHome, full))
        {
            // Links into the old home are removed (only the link, never a target) so the sync links them to the new one.
            foreach (var (name, entry) in cfg.Profiles)
            {
                if (entry.Home) continue;
                foreach (var d in SharedItems.Dirs.Where(d => !entry.Overrides.Contains(d)))
                {
                    var link = Path.Combine(paths.ProfileDir(name), d);
                    try
                    {
                        if (Junction.GetTarget(link) is { } target && SamePath(target, Path.Combine(oldHome, d)))
                            Directory.Delete(link); // non-recursive: removes only the junction
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        report.Problems.Add($"{name}: could not sync {d}: {e.Message}");
                    }
                }
            }
        }
        var sync = NewSyncer(cfg).SyncAll();
        report.Notices.AddRange(sync.Notices);
        report.Problems.AddRange(sync.Problems);
        return new HomeResult(report, full, homeJson, File.Exists(homeJson));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
