namespace Clauderoo.Core;

public record CheckResult(bool Ok, string Message);

public partial class ProfileManager
{
    private const string HomeMessage = "The home profile always uses its own Claude login.";

    private static ProfileEntry NonHome(ClauderooConfig cfg, string name)
    {
        var e = Entry(cfg, name);
        if (e.Home) throw new ClauderooException(HomeMessage);
        return e;
    }

    public SyncReport SetProvider(string name, string provider, VertexSettings? vertex = null, string? apiKey = null)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        var dir = paths.ProfileDir(name);

        switch (provider)
        {
            case Providers.Login:
                break;
            case Providers.Vertex:
                if (vertex == null || string.IsNullOrWhiteSpace(vertex.Project) || string.IsNullOrWhiteSpace(vertex.Region))
                    throw new ClauderooException("Vertex needs a project and a region.");
                break;
            case Providers.ApiKey:
                if (string.IsNullOrWhiteSpace(apiKey) && !secrets.Exists(dir))
                    throw new ClauderooException("Enter an API key.");
                break;
            default:
                throw new ClauderooException($"Unknown provider '{provider}'. Known: login, vertex, apikey.");
        }

        if (provider == Providers.ApiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                Directory.CreateDirectory(dir);
                secrets.Save(dir, apiKey);
            }
        }
        else
        {
            secrets.Delete(dir);
        }

        entry.Provider = provider == Providers.Login ? null : provider;
        if (provider == Providers.Vertex)
        {
            entry.Vertex = new VertexSettings
            {
                Project = vertex!.Project.Trim(),
                Region = vertex.Region.Trim(),
                SonnetModel = Blank(vertex.SonnetModel),
                OpusModel = Blank(vertex.OpusModel),
                HaikuModel = Blank(vertex.HaikuModel),
            };
        }
        ConfigStore.Save(paths, cfg);
        return NewSyncer(cfg).Sync(name);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public IReadOnlyDictionary<string, string> GetEnv(string name)
    {
        var entry = NonHome(Config, name);
        return new Dictionary<string, string>(entry.Env, StringComparer.Ordinal);
    }

    public SyncReport SetEnv(string name, IEnumerable<KeyValuePair<string, string>> set, IEnumerable<string> unset)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        var setList = set.ToList();
        foreach (var kv in setList) ProviderOverlay.ValidateEnvName(kv.Key);
        foreach (var k in unset) entry.Env.Remove(k);
        foreach (var kv in setList) entry.Env[kv.Key] = kv.Value;
        ConfigStore.Save(paths, cfg);
        return NewSyncer(cfg).Sync(name);
    }

    public SyncReport ReplaceEnv(string name, IReadOnlyDictionary<string, string> envVars)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        foreach (var k in envVars.Keys) ProviderOverlay.ValidateEnvName(k);
        entry.Env = new Dictionary<string, string>(envVars, StringComparer.Ordinal);
        ConfigStore.Save(paths, cfg);
        return NewSyncer(cfg).Sync(name);
    }

    /// <summary>The profile's saved API key, or null. Throws for an unknown profile or the home profile.</summary>
    public string? GetApiKey(string name)
    {
        NonHome(Config, name);
        return secrets.Load(paths.ProfileDir(name));
    }

    /// <summary>True when the profile has a saved API key; false for an unknown profile or the home profile.</summary>
    public bool HasApiKey(string name)
    {
        var cfg = Config;
        return cfg.Profiles.TryGetValue(name, out var e) && !e.Home && secrets.Exists(paths.ProfileDir(name));
    }

    public CheckResult Check(string name)
    {
        var cfg = Config;
        var entry = Entry(cfg, name);
        var dir = entry.Home ? cfg.HomeDir : paths.ProfileDir(name);

        switch (entry.Home ? Providers.Login : Providers.Of(entry))
        {
            case Providers.Vertex:
                if (gcloud.Find() == null)
                    return new CheckResult(false, "gcloud not found — install it with: winget install Google.CloudSDK");
                var (exit, stdout, stderr) = gcloud.Run("auth", "application-default", "print-access-token");
                if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
                    return new CheckResult(true, "Google sign-in OK.");
                var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? "";
                return new CheckResult(false, $"Google sign-in failed: {line}");
            case Providers.ApiKey:
                return secrets.Exists(dir)
                    ? new CheckResult(true, "API key saved.")
                    : new CheckResult(false, "No API key saved.");
            default:
                var json = entry.Home ? cfg.HomeJson : Path.Combine(dir, ".claude.json");
                var email = ReadEmail(json);
                return email != null
                    ? new CheckResult(true, $"Logged in as {email}.")
                    : new CheckResult(false, "Not logged in — start a session to sign in.");
        }
    }
}
