using System.Text.Json;

namespace Clauderoo.Core;

public record ProfileInfo(string Name, bool IsHome, bool IsDefault, string? Email,
    IReadOnlyList<string> Overrides, string Dir, string Provider = Providers.Login, string? ProviderDetail = null);

public record StatusReport(string? DefaultProfile, string? UserEnvValue, bool EnvMatchesDefault,
    IReadOnlyList<string> Problems);

public partial class ProfileManager(ClauderooPaths paths, IEnvStore env, Func<DateTime> now,
    ISecretStore secrets, IGcloud gcloud)
{
    public ProfileManager(ClauderooPaths paths, IEnvStore env, Func<DateTime> now)
        : this(paths, env, now, new DpapiSecretStore(), new Gcloud()) { }

    /// <summary>The exe that apiKeyHelper runs; handed to every Syncer.</summary>
    public string HelperExe { get; set; } = HelperExePath.Resolve();

    private Syncer NewSyncer(ClauderooConfig cfg) => new(cfg, paths, now) { HelperExe = HelperExe };

    private const string EnvVar = "CLAUDE_CONFIG_DIR";

    private string Stamp => now().ToString("yyyyMMdd-HHmmss");

    public ClauderooPaths Paths => paths;

    public SyncReport SyncAll() => NewSyncer(Config).SyncAll();

    public ClauderooConfig Config =>
        ConfigStore.Load(paths) ?? throw new ClauderooException("Run 'clauderoo init' first.");

    public void Init(string name = "personal", string? homeDir = null)
    {
        ProfileName.Validate(name);
        if (File.Exists(paths.ConfigFile))
            throw new ClauderooException("clauderoo is already initialised.");

        string home, homeJson;
        if (homeDir == null)
        {
            var v = env.GetProcess(EnvVar);
            if (string.IsNullOrEmpty(v)) v = env.GetUser(EnvVar);
            if (!string.IsNullOrEmpty(v))
                throw new ClauderooException(
                    $"{EnvVar} is set to '{v}'. Clear it, or run 'clauderoo init --home <path>'.");
            home = paths.DefaultHomeDir;
            homeJson = paths.DefaultHomeJson;
        }
        else
        {
            home = homeDir;
            homeJson = Path.Combine(homeDir, ".claude.json");
        }

        if (!Directory.Exists(home))
            throw new ClauderooException($"Home folder not found: {home}");

        ConfigStore.Save(paths, new ClauderooConfig
        {
            DefaultProfile = name,
            HomeDir = home,
            HomeJson = homeJson,
            Profiles = { [name] = new ProfileEntry { Home = true } },
        });
    }

    public SyncReport Add(string name)
    {
        ProfileName.Validate(name);
        var cfg = Config;
        if (cfg.Profiles.ContainsKey(name))
            throw new ClauderooException($"Profile '{name}' already exists.");
        Directory.CreateDirectory(paths.ProfileDir(name));
        cfg.Profiles[name] = new ProfileEntry();
        ConfigStore.Save(paths, cfg);
        return NewSyncer(cfg).Sync(name);
    }

    public void Remove(string name, bool force = false)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        if (entry.Home) throw new ClauderooException("Cannot remove the home profile.");
        if (cfg.DefaultProfile == name)
            throw new ClauderooException($"'{name}' is the default profile. Change the default first.");

        var dir = paths.ProfileDir(name);
        if (!force && PluginRecordsReference(cfg.HomeDir, dir))
            throw new ClauderooException(
                $"{name}'s folder is referenced by shared plugin records (installed from the {name} profile). " +
                $"Reinstall those plugins from {cfg.HomeDir}, or run 'clauderoo remove {name} --force'.");

        secrets.Delete(dir);
        if (Directory.Exists(dir))
        {
            try
            {
                // Unlink shared-folder junctions first so nothing that later deletes the moved
                // folder (e.g. PowerShell 5.1 Remove-Item -Recurse) can follow them into home.
                RemoveNestedLinks(dir);
                Directory.CreateDirectory(paths.RemovedDir);
                Directory.Move(dir, Path.Combine(paths.RemovedDir, $"{name}-{Stamp}"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ClauderooException(
                    $"Couldn't move {name}'s folder — close {name}'s Claude sessions and try again.");
            }
        }
        cfg.Profiles.Remove(name);
        ConfigStore.Save(paths, cfg);
    }

    private static readonly string[] PluginRecordFiles = ["installed_plugins.json", "known_marketplaces.json"];

    // True when a string in home's shared plugin records points inside the profile folder.
    private static bool PluginRecordsReference(string homeDir, string profileDir)
    {
        var needle = profileDir.TrimEnd('\\', '/').Replace('/', '\\');
        foreach (var f in PluginRecordFiles)
        {
            var file = Path.Combine(homeDir, "plugins", f);
            try
            {
                if (!File.Exists(file)) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                if (AnyStringIn(doc.RootElement, needle)) return true;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool AnyStringIn(JsonElement el, string needle)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var v = (el.GetString() ?? "").Replace('/', '\\');
                var i = 0;
                while ((i = v.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var end = i + needle.Length;
                    if (end == v.Length || v[end] == '\\') return true;
                    i = end;
                }
                return false;
            case JsonValueKind.Array:
                return el.EnumerateArray().Any(x => AnyStringIn(x, needle));
            case JsonValueKind.Object:
                return el.EnumerateObject().Any(p => AnyStringIn(p.Value, needle));
            default:
                return false;
        }
    }

    public IReadOnlyList<ProfileInfo> List()
    {
        var cfg = Config;
        return cfg.Profiles.Select(kv =>
        {
            var dir = kv.Value.Home ? cfg.HomeDir : paths.ProfileDir(kv.Key);
            var json = kv.Value.Home ? cfg.HomeJson : Path.Combine(dir, ".claude.json");
            var provider = kv.Value.Home ? Providers.Login : Providers.Of(kv.Value);
            var detail = provider == Providers.Vertex ? kv.Value.Vertex?.Project : null;
            return new ProfileInfo(kv.Key, kv.Value.Home, cfg.DefaultProfile == kv.Key,
                ReadEmail(json), kv.Value.Overrides.ToArray(), dir, provider, detail);
        }).ToList();
    }

    public ProfileEntry Get(string name) => Entry(Config, name);

    public void Own(string name, string item)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        if (entry.Home) throw new ClauderooException("The home profile owns everything already.");
        if (!SharedItems.IsKnown(item)) throw new ClauderooException(UnknownItem(item));

        if (!entry.Overrides.Contains(item)) entry.Overrides.Add(item);

        if (SharedItems.IsDir(item))
        {
            var link = Path.Combine(paths.ProfileDir(name), item);
            var source = Path.Combine(cfg.HomeDir, item);
            var oldTarget = Junction.GetTarget(link);
            if (oldTarget != null)
            {
                var tmp = link + ".tmp-copy";
                if (Directory.Exists(tmp)) DeleteOwnCopy(tmp);
                try
                {
                    if (Directory.Exists(source)) CopyDir(source, tmp);
                    else Directory.CreateDirectory(tmp);
                    Directory.Delete(link); // non-recursive: removes only the junction, never the target
                    Directory.Move(tmp, link);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ClauderooException)
                {
                    DeleteOwnCopy(tmp);
                    if (!Directory.Exists(link)) Junction.Create(link, oldTarget);
                    throw new ClauderooException($"Could not copy {item} for '{name}': {e.Message}");
                }
            }
            else if (!Directory.Exists(link))
            {
                if (Directory.Exists(source)) CopyDir(source, link);
                else Directory.CreateDirectory(link);
            }
        }
        ConfigStore.Save(paths, cfg);
    }

    public SyncReport Share(string name, string item)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        if (entry.Home) throw new ClauderooException("The home profile is the source of shared items.");
        if (!SharedItems.IsKnown(item)) throw new ClauderooException(UnknownItem(item));

        entry.Overrides.Remove(item);

        if (SharedItems.IsDir(item))
        {
            var link = Path.Combine(paths.ProfileDir(name), item);
            if (Directory.Exists(link) && Junction.GetTarget(link) == null)
                Directory.Move(link, $"{link}.bak-{Stamp}");
        }
        ConfigStore.Save(paths, cfg);
        return NewSyncer(cfg).Sync(name);
    }

    public StatusReport Status()
    {
        var cfg = Config;
        var report = NewSyncer(cfg).SyncAll();

        var userValue = env.GetUser(EnvVar);
        string? expected = null;
        if (cfg.DefaultProfile != null && cfg.Profiles.TryGetValue(cfg.DefaultProfile, out var def) && !def.Home)
            expected = paths.ProfileDir(cfg.DefaultProfile);

        var matches = expected == null
            ? string.IsNullOrEmpty(userValue)
            : !string.IsNullOrEmpty(userValue) && Norm(userValue) == Norm(expected);

        return new StatusReport(cfg.DefaultProfile, userValue, matches, report.Problems);
    }

    private static string Norm(string p) =>
        p.TrimEnd('\\', '/').ToUpperInvariant();

    private static string UnknownItem(string item) =>
        $"Unknown item '{item}'. Known: {string.Join(", ", SharedItems.Dirs.Concat(SharedItems.Files).Append(SharedItems.McpServers))}.";

    private static ProfileEntry Entry(ClauderooConfig cfg, string name) =>
        cfg.Profiles.TryGetValue(name, out var e)
            ? e
            : throw new ClauderooException(
                $"Unknown profile '{name}'. Known: {string.Join(", ", cfg.Profiles.Keys)}.");

    private static string? ReadEmail(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.TryGetProperty("oauthAccount", out var acct)
                   && acct.TryGetProperty("emailAddress", out var email)
                   && email.ValueKind == JsonValueKind.String
                ? email.GetString()
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(d));
            var linkTarget = Junction.GetTarget(d);
            if (linkTarget != null) Junction.Create(target, linkTarget); // never follow links
            else CopyDir(d, target);
        }
    }

    // Deletes a copy this class made: nested junctions are removed non-recursively first.
    private static void DeleteOwnCopy(string dir)
    {
        if (!Directory.Exists(dir)) return;
        if (Junction.GetTarget(dir) != null) { Directory.Delete(dir); return; }
        RemoveNestedLinks(dir);
        Directory.Delete(dir, recursive: true);
    }

    private static void RemoveNestedLinks(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (Junction.GetTarget(sub) != null) Directory.Delete(sub);
            else RemoveNestedLinks(sub);
        }
    }
}
