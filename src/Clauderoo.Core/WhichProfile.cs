namespace Clauderoo.Core;

/// <param name="Source">"session", "configDir", "default" or "home".</param>
public record WhichResult(string Profile, string Source, bool Isolated);

public static class WhichProfile
{
    public const string SessionVar = "CLAUDEROO_PROFILE";
    private const string ConfigDirVar = "CLAUDE_CONFIG_DIR";

    public static WhichResult Resolve(ClauderooConfig cfg, ClauderooPaths paths,
        Func<string, string?> processEnv, IEnvStore userEnv)
    {
        var configDir = processEnv(ConfigDirVar);

        // 1. A session started by clauderoo says so itself.
        var session = processEnv(SessionVar);
        if (!string.IsNullOrEmpty(session) && cfg.Profiles.ContainsKey(session))
            return new WhichResult(session, "session", IsIsolated(paths, session, configDir));

        // 2. CLAUDE_CONFIG_DIR matches a profile folder (or an isolated one).
        if (Match(cfg, paths, configDir) is { } hit)
            return new WhichResult(hit.Profile, "configDir", hit.Isolated);

        // 3. The default profile, as new terminals see it through the user env var.
        if (Match(cfg, paths, userEnv.GetUser(ConfigDirVar)) is { } def)
            return new WhichResult(def.Profile, "default", false);

        // 4. Nothing set: the home profile.
        var home = cfg.Profiles.FirstOrDefault(kv => kv.Value.Home).Key ?? "home";
        return new WhichResult(home, "home", false);
    }

    public static string Describe(WhichResult r) => r.Source switch
    {
        "session" => $"{r.Profile} (started by clauderoo{(r.Isolated ? ", isolated" : "")})",
        "configDir" => $"{r.Profile} (CLAUDE_CONFIG_DIR{(r.Isolated ? ", isolated" : "")})",
        "default" => $"{r.Profile} (default for new terminals)",
        _ => $"{r.Profile} (home profile, no CLAUDE_CONFIG_DIR)",
    };

    private static (string Profile, bool Isolated)? Match(ClauderooConfig cfg, ClauderooPaths paths, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (entry.Home) continue;
            if (Same(dir, paths.ProfileDir(name))) return (name, false);
            if (Same(dir, paths.IsolatedDir(name))) return (name, true);
        }
        return null;
    }

    private static bool IsIsolated(ClauderooPaths paths, string profile, string? configDir) =>
        !string.IsNullOrWhiteSpace(configDir) && Same(configDir, paths.IsolatedDir(profile));

    private static bool Same(string a, string b)
    {
        try { return string.Equals(FolderMap.Normalize(a), FolderMap.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
