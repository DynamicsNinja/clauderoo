using System.Text.Json;

namespace Clauderoo.Core;

public record ProfileInfo(string Name, bool IsHome, bool IsDefault, string? Email,
    IReadOnlyList<string> Overrides, string Dir);

public record StatusReport(string? DefaultProfile, string? UserEnvValue, bool EnvMatchesDefault,
    IReadOnlyList<string> Problems);

public partial class ProfileManager(ClauderooPaths paths, IEnvStore env, Func<DateTime> now)
{
    private const string EnvVar = "CLAUDE_CONFIG_DIR";

    private string Stamp => now().ToString("yyyyMMdd-HHmmss");

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

    public void Add(string name)
    {
        ProfileName.Validate(name);
        var cfg = Config;
        if (cfg.Profiles.ContainsKey(name))
            throw new ClauderooException($"Profile '{name}' already exists.");
        Directory.CreateDirectory(paths.ProfileDir(name));
        cfg.Profiles[name] = new ProfileEntry();
        ConfigStore.Save(paths, cfg);
        new Syncer(cfg, paths, now).Sync(name);
    }

    public void Remove(string name)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        if (entry.Home) throw new ClauderooException("Cannot remove the home profile.");
        if (cfg.DefaultProfile == name)
            throw new ClauderooException($"'{name}' is the default profile. Change the default first.");

        var dir = paths.ProfileDir(name);
        if (Directory.Exists(dir))
        {
            Directory.CreateDirectory(paths.RemovedDir);
            Directory.Move(dir, Path.Combine(paths.RemovedDir, $"{name}-{Stamp}"));
        }
        cfg.Profiles.Remove(name);
        ConfigStore.Save(paths, cfg);
    }

    public IReadOnlyList<ProfileInfo> List()
    {
        var cfg = Config;
        return cfg.Profiles.Select(kv =>
        {
            var dir = kv.Value.Home ? cfg.HomeDir : paths.ProfileDir(kv.Key);
            var json = kv.Value.Home ? cfg.HomeJson : Path.Combine(dir, ".claude.json");
            return new ProfileInfo(kv.Key, kv.Value.Home, cfg.DefaultProfile == kv.Key,
                ReadEmail(json), kv.Value.Overrides.ToArray(), dir);
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
            if (Junction.GetTarget(link) != null)
                Directory.Delete(link); // non-recursive: removes only the junction, never the target

            var source = Path.Combine(cfg.HomeDir, item);
            if (!Directory.Exists(link))
            {
                if (Directory.Exists(source)) CopyDir(source, link);
                else Directory.CreateDirectory(link);
            }
        }
        ConfigStore.Save(paths, cfg);
    }

    public void Share(string name, string item)
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
        new Syncer(cfg, paths, now).Sync(name);
    }

    public StatusReport Status()
    {
        var cfg = Config;
        var report = new Syncer(cfg, paths, now).SyncAll();

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
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
