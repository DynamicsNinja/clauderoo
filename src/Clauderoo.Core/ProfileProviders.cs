namespace Clauderoo.Core;

/// <summary><see cref="Verified"/> is false when Ok only means "nothing is wrong that we can see" (e.g. a saved API key
/// that was never tried); the UI shows such results neutrally instead of as a success.</summary>
public record CheckResult(bool Ok, string Message, bool Verified = true);

public partial class ProfileManager
{
    private const string HomeMessage = "The home profile always uses its own Claude login.";

    private static ProfileEntry NonHome(ClauderooConfig cfg, string name)
    {
        var e = Entry(cfg, name);
        if (e.Home) throw new ClauderooException(HomeMessage);
        return e;
    }

    public SyncReport SetProvider(string name, string provider, VertexSettings? vertex = null, string? apiKey = null,
        string? token = null)
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
            case Providers.Token:
                token = token?.Trim();
                // No token given keeps the saved one; the message never echoes what was entered.
                if (string.IsNullOrEmpty(token) ? !tokens.Exists(dir) : !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
                    throw new ClauderooException(BadTokenMessage);
                break;
            default:
                throw new ClauderooException($"Unknown provider '{provider}'. Known: {KnownProviders}.");
        }

        // Save the new secret before deleting the other one: if saving fails, the old secret is still there.
        if (provider == Providers.ApiKey && !string.IsNullOrWhiteSpace(apiKey))
        {
            Directory.CreateDirectory(dir);
            secrets.Save(dir, apiKey);
        }
        if (provider == Providers.Token && !string.IsNullOrEmpty(token))
        {
            Directory.CreateDirectory(dir);
            tokens.Save(dir, token);
        }
        if (provider != Providers.ApiKey) secrets.Delete(dir);
        if (provider != Providers.Token) tokens.Delete(dir);

        cfg = ConfigStore.Update(paths, c =>
        {
            var e = NonHome(c, name);
            e.Provider = provider == Providers.Login ? null : provider;
            if (provider == Providers.Vertex)
            {
                e.Vertex = new VertexSettings
                {
                    Project = vertex!.Project.Trim(),
                    Region = vertex.Region.Trim(),
                    SonnetModel = Blank(vertex.SonnetModel),
                    OpusModel = Blank(vertex.OpusModel),
                    HaikuModel = Blank(vertex.HaikuModel),
                };
            }
        });
        AppLog.Info($"Profile {name} now uses {Providers.DisplayName(provider)}.");
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
        var unsetList = unset.ToList();
        cfg = ConfigStore.Update(paths, c =>
        {
            var e = NonHome(c, name);
            foreach (var k in unsetList) e.Env.Remove(k);
            foreach (var kv in setList) e.Env[kv.Key] = kv.Value;
        });
        return NewSyncer(cfg).Sync(name);
    }

    public SyncReport ReplaceEnv(string name, IReadOnlyDictionary<string, string> envVars)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        foreach (var k in envVars.Keys) ProviderOverlay.ValidateEnvName(k);
        cfg = ConfigStore.Update(paths, c => { NonHome(c, name).Env = new Dictionary<string, string>(envVars, StringComparer.Ordinal); });
        return NewSyncer(cfg).Sync(name);
    }

    /// <summary>The profile's saved API key, or null. Throws for an unknown profile or the home profile.</summary>
    public string? GetApiKey(string name)
    {
        NonHome(Config, name);
        return secrets.Load(paths.ProfileDir(name));
    }

    internal const string TokenPrefix = "sk-ant-oat";
    internal const string TokenEnvVar = "CLAUDE_CODE_OAUTH_TOKEN";
    internal const string KnownProviders = "login, vertex, apikey, token";
    private const string BadTokenMessage =
        "That doesn't look like a Claude token (it should start with sk-ant-oat). Create one with 'claude setup-token'.";

    /// <summary>
    /// The profile's decrypted Claude token, or null. Only the launch path (<see cref="WithToken"/>) may call this:
    /// the token is never printed, logged, written to settings or put in a message.
    /// </summary>
    internal string? GetToken(string name)
    {
        NonHome(Config, name);
        return tokens.Load(paths.ProfileDir(name));
    }

    /// <summary>
    /// For a token profile, <paramref name="spec"/> with the token added to its environment (decrypted just before
    /// launch); any other profile's spec is returned unchanged. Throws when a token profile has no saved token.
    /// </summary>
    internal LaunchSpec WithToken(LaunchSpec spec, string profile)
    {
        var entry = Entry(Config, profile);
        if (entry.Home || Providers.Of(entry) != Providers.Token) return spec;
        var token = GetToken(profile);
        if (string.IsNullOrEmpty(token))
            throw new ClauderooException(
                $"No Claude token is saved for {profile}. Set one with 'cr provider {profile} token'.");
        var env = new Dictionary<string, string>(spec.ExtraEnv ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [TokenEnvVar] = token,
        };
        return spec with { ExtraEnv = env };
    }

    /// <summary>True when the profile has a saved Claude token; false for an unknown profile or the home profile.</summary>
    public bool HasToken(string name)
    {
        var cfg = Config;
        return cfg.Profiles.TryGetValue(name, out var e) && !e.Home && tokens.Exists(paths.ProfileDir(name));
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
                    ? new CheckResult(true, "Key saved — not verified. Start a session to test it.", Verified: false)
                    : new CheckResult(false, "No API key saved.");
            case Providers.Token:
                return tokens.Exists(dir)
                    ? new CheckResult(true, "Token saved — not verified. Start a session to test it.", Verified: false)
                    : new CheckResult(false, $"No Claude token saved. Set one with 'cr provider {name} token'.");
            default:
                var json = entry.Home ? cfg.HomeJson : Path.Combine(dir, ".claude.json");
                var email = ReadEmail(json);
                return email != null
                    ? new CheckResult(true, $"Logged in as {email}.")
                    : new CheckResult(false, "Not logged in — start a session to sign in.");
        }
    }
}
