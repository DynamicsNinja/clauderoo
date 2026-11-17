namespace Clauderoo.Core;

/// <summary>
/// What a delegated run (cr mcp's delegate tool) must not inherit from the calling session's environment: anything
/// that picks a provider, a model, an account or a Claude Code session. Without this a Vertex caller delegating to a
/// login profile would make the child run on the caller's Vertex project. Normal launches don't use it.
/// </summary>
public static class DelegateEnv
{
    /// <summary>Provider and model switches that clauderoo doesn't manage itself but still steer claude.</summary>
    public static IReadOnlySet<string> FixedNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_USE_BEDROCK",
        "ANTHROPIC_MODEL",
    };

    /// <summary>Claude Code's own session markers and switches (CLAUDECODE, CLAUDE_CODE_ENTRYPOINT, CLAUDE_CODE_SSE_PORT…).</summary>
    public static IReadOnlyList<string> Prefixes { get; } = ["CLAUDECODE", "CLAUDE_CODE_"];

    /// <summary>
    /// True when <paramref name="name"/> is dropped from a delegated run's inherited environment: a provider-reserved
    /// name, a key some profile sets in its Env, one of <see cref="FixedNames"/>, or anything starting with one of
    /// <see cref="Prefixes"/>. Case-insensitive, as Windows environment names are.
    /// </summary>
    public static bool Drops(string name, IReadOnlySet<string> profileEnvKeys) =>
        !KeptNames.Contains(name) && (ProviderOverlay.ReservedEnv.Contains(name)
        || FixedNames.Contains(name)
        || profileEnvKeys.Contains(name)
        || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Machine settings that only look like Claude Code switches: they say where tools live, not which account or
    /// provider to use, so a delegated run keeps them (without Git Bash's path claude can't run on Windows).
    /// </summary>
    public static IReadOnlySet<string> KeptNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDE_CODE_GIT_BASH_PATH",
    };

    /// <summary>The filter for <see cref="LaunchSpec.DropEnv"/>, with every profile's Env keys taken from <paramref name="cfg"/>.</summary>
    public static Func<string, bool> Filter(ClauderooConfig cfg)
    {
        var keys = new HashSet<string>(cfg.Profiles.Values.SelectMany(p => p.Env.Keys), StringComparer.OrdinalIgnoreCase);
        return name => Drops(name, keys);
    }
}
