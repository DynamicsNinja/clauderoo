using System.Globalization;
using System.Net;

namespace Clauderoo.Core;

/// <summary>Tries an Anthropic API key with one authenticated call that uses no quota (the models list).</summary>
public interface IApiKeyTester
{
    /// <returns>valid true: the key was accepted; false: it was rejected; null: couldn't tell (error says why). The
    /// error never contains the key.</returns>
    Task<(bool? valid, string? error)> TestAsync(string apiKey, Uri baseUrl, CancellationToken ct);
}

public sealed class HttpApiKeyTester : IApiKeyTester
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout };
    private readonly HttpClient _http;

    public HttpApiKeyTester() { _http = SharedHttp; }
    internal HttpApiKeyTester(HttpMessageHandler handler) { _http = new HttpClient(handler) { Timeout = Timeout }; }

    public async Task<(bool? valid, string? error)> TestAsync(string apiKey, Uri baseUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "v1/models?limit=1"));
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => (true, null),
                // Rate limited: the key got past authentication.
                HttpStatusCode.TooManyRequests => (true, null),
                HttpStatusCode.Unauthorized => (false, "HTTP 401: the key was rejected"),
                HttpStatusCode.Forbidden => (false, "HTTP 403: the key isn't allowed to use the API"),
                _ => (null, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var msg = e is TaskCanceledException ? $"no answer within {(int)Timeout.TotalSeconds} s" : e.GetType().Name + ": " + e.Message;
            return (null, Secrets.Scrub(msg, apiKey));
        }
    }
}

public partial class ProfileManager
{
    /// <summary>A token from `claude setup-token` lasts one year (Claude Code authentication docs).</summary>
    public const int TokenLifetimeDays = 365;
    /// <summary>Warn this many days after a token was saved unless config.json sets tokenWarnAfterDays.</summary>
    public const int DefaultTokenWarnAfterDays = 335;
    internal const string DefaultAnthropicBaseUrl = "https://api.anthropic.com/";

    private IApiKeyTester keyTester = new HttpApiKeyTester();

    /// <summary>For tests: the API-key tester the live check uses.</summary>
    internal IApiKeyTester KeyTester { set => keyTester = value; }

    /// <summary>For tests: the claude runner the token check uses.</summary>
    internal IClaudeRunner ClaudeRunner { set => claudeRunner = value; }

    private DateTimeOffset NowUtc => new DateTimeOffset(now()).ToUniversalTime();

    /// <summary>When the API key or token the profile uses now was saved, or null (none saved, or unknown).</summary>
    public DateTimeOffset? SecretSavedAt(string name) => SecretSavedAt(name, Providers.Of(NonHome(Config, name)));

    /// <summary>
    /// When the profile's saved <paramref name="kind"/> secret (apikey or token) was saved, whether or not the profile
    /// uses it now (a kept one); null when none is saved or the time is unknown, and for any other kind.
    /// </summary>
    public DateTimeOffset? SecretSavedAt(string name, string kind)
    {
        NonHome(Config, name);
        var dir = paths.ProfileDir(name);
        return kind switch
        {
            Providers.Token => tokens.SavedAt(dir),
            Providers.ApiKey => secrets.SavedAt(dir),
            _ => null,
        };
    }

    /// <summary>The rows <see cref="CheckSecretAsync"/> reports, in order, for an apikey or token profile.</summary>
    public static IReadOnlyList<(string Key, string Label)> SecretCheckPlan(string provider) => provider == Providers.Token
        ? [("saved", "Claude token saved"), ("age", "Token age"), ("claude", "Claude Code installed"), ("live", "Token answers a test prompt")]
        : [("saved", "API key saved"), ("live", "Anthropic API accepts the key")];

    private static int WarnAfterDays(ClauderooConfig cfg) =>
        cfg.TokenWarnAfterDays is { } d and > 0 ? d : DefaultTokenWarnAfterDays;

    private static string Day(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private int AgeDays(DateTimeOffset savedAt) => Math.Max(0, (int)Math.Floor((NowUtc - savedAt).TotalDays));

    /// <summary>A plain warning when a token profile's token is near or past its one-year lifetime, else null.</summary>
    private string? TokenAgeWarning(ClauderooConfig cfg, string name, DateTimeOffset? savedAt)
    {
        if (savedAt is not { } at) return null;
        var age = AgeDays(at);
        if (age >= TokenLifetimeDays)
            return $"{name}'s Claude token was saved on {Day(at)} and has probably expired (tokens last a year). " +
                   $"Create a new one with 'claude setup-token', then run 'cr provider {name} token'.";
        if (age >= WarnAfterDays(cfg))
            return $"{name}'s Claude token was saved on {Day(at)} and expires around {Day(at.AddDays(TokenLifetimeDays))}. " +
                   $"Create a new one with 'claude setup-token', then run 'cr provider {name} token'.";
        return null;
    }

    /// <summary>One problem per token profile whose token is near or past its lifetime (for status and the tray).</summary>
    public IReadOnlyList<Problem> TokenAgeProblems()
    {
        var cfg = Config;
        var list = new List<Problem>();
        foreach (var (name, entry) in cfg.Profiles)
        {
            if (entry.Home || Providers.Of(entry) != Providers.Token) continue;
            DateTimeOffset? at;
            try { at = tokens.SavedAt(paths.ProfileDir(name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (TokenAgeWarning(cfg, name, at) is not { } text) continue;
            var expired = AgeDays(at!.Value) >= TokenLifetimeDays;
            list.Add(new Problem(name, expired ? $"{name}'s Claude token has probably expired" : $"{name}'s Claude token expires soon",
                text, null));
        }
        return list;
    }

    // The saved-secret part of Check: the date it was saved, and for a token any age warning.
    private CheckResult SecretSaved(ClauderooConfig cfg, string name, string dir, bool token)
    {
        var at = token ? tokens.SavedAt(dir) : secrets.SavedAt(dir);
        var what = token ? "Token" : "Key";
        var msg = $"{what} saved — not verified. Start a session to test it.";
        if (at is { } t) msg += $" Saved on {Day(t)} ({AgeDays(t)} days ago).";
        var warn = token ? TokenAgeWarning(cfg, name, at) : null;
        if (warn != null) msg += " " + warn;
        return new CheckResult(true, msg, Verified: false, Warning: warn);
    }

    /// <summary>
    /// The live check for an API-key or token profile, reported like <see cref="CheckSetupAsync"/> (every planned row
    /// Pending first, then each result under its key). An API key is tried against the models list (no quota); a token
    /// can only make model requests, so it answers one tiny prompt through `claude -p` (a few tokens of its quota).
    /// Nothing is changed and the secret never reaches a result.
    /// </summary>
    public async Task<IReadOnlyList<CheckItem>> CheckSecretAsync(string name, CancellationToken ct = default,
        IProgress<CheckItem>? progress = null)
    {
        var cfg = Config;
        var entry = NonHome(cfg, name);
        var provider = Providers.Of(entry);
        if (provider is not (Providers.ApiKey or Providers.Token))
            throw new ClauderooException($"Profile '{name}' doesn't use an API key or a Claude token.");
        var dir = paths.ProfileDir(name);
        var token = provider == Providers.Token;

        var plan = SecretCheckPlan(provider);
        foreach (var (key, label) in plan) progress?.Report(new CheckItem(CheckStatus.Pending, label, null) { Key = key });
        var rows = new List<CheckItem>();
        void Put(CheckItem item) { rows.Add(item); progress?.Report(item); }
        void SkipRest(int from)
        {
            foreach (var (key, label) in plan.Skip(from)) Put(new CheckItem(CheckStatus.Skipped, label, null) { Key = key });
        }

        var store = token ? tokens : secrets;
        string? secret;
        try { secret = store.Load(dir); }
        catch (ClauderooException e) { Put(new CheckItem(CheckStatus.Fail, plan[0].Item2, e.Message) { Key = "saved" }); SkipRest(1); return rows; }
        var setCmd = $"cr provider {name} {(token ? "token" : "apikey")}";
        if (string.IsNullOrEmpty(secret))
        {
            Put(new CheckItem(CheckStatus.Fail, plan[0].Item2, $"Set one with '{setCmd}'.") { Key = "saved" });
            SkipRest(1);
            return rows;
        }
        var at = store.SavedAt(dir);
        Put(new CheckItem(CheckStatus.Ok, at is { } t ? $"{plan[0].Item2} on {Day(t)}" : plan[0].Item2, null) { Key = "saved" });

        if (!token)
        {
            Put(await LiveKeyAsync(entry, secret, setCmd, ct).ConfigureAwait(false));
            return rows;
        }

        if (at is not { } savedAt)
            Put(new CheckItem(CheckStatus.Skipped, "Token age unknown", null) { Key = "age" });
        else if (TokenAgeWarning(cfg, name, savedAt) is { } warn)
            Put(new CheckItem(AgeDays(savedAt) >= TokenLifetimeDays ? CheckStatus.Fail : CheckStatus.Warn,
                $"Token is {AgeDays(savedAt)} days old (tokens last a year)", warn) { Key = "age" });
        else
            Put(new CheckItem(CheckStatus.Ok, $"Token is {AgeDays(savedAt)} days old; expires around {Day(savedAt.AddDays(TokenLifetimeDays))}", null) { Key = "age" });

        var claude = claudeRunner.Find();
        if (claude == null)
        {
            Put(new CheckItem(CheckStatus.Fail, "Claude Code installed", "Install Claude Code, then open a new terminal.") { Key = "claude" });
            SkipRest(3);
            return rows;
        }
        Put(new CheckItem(CheckStatus.Ok, "Claude Code installed", null) { Key = "claude" });
        ct.ThrowIfCancellationRequested();
        progress?.Report(new CheckItem(CheckStatus.Running, "the token with a tiny prompt", null) { Key = "live" });
        var args = new List<string> { "-p", "Reply with exactly: OK", "--strict-mcp-config", "--no-session-persistence", "--model", "haiku" };
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [TokenEnvVar] = secret };
        var (exit, stdout, stderr) = await claudeRunner.RunAsync(claude, args, dir, TimeSpan.FromSeconds(60), ct, env).ConfigureAwait(false);
        if (exit == 0 && stdout.Contains("OK", StringComparison.Ordinal))
            Put(new CheckItem(CheckStatus.Ok, "Token answers a test prompt", null) { Key = "live" });
        else
        {
            var last = Secrets.Scrub(LineOf(stdout + "\n" + stderr, first: false), secret);
            if (last.Length > 200) last = last[..200];
            var label = exit == -1 && last.StartsWith("timed out", StringComparison.OrdinalIgnoreCase)
                ? "The token didn't answer within 60 seconds"
                : "The token didn't answer a test prompt";
            Put(new CheckItem(CheckStatus.Fail, label,
                $"It may have expired or been revoked. Create a new one with 'claude setup-token', then run '{setCmd}'.",
                last.Length > 0 ? last : null) { Key = "live" });
        }
        return rows;
    }

    private async Task<CheckItem> LiveKeyAsync(ProfileEntry entry, string key, string setCmd, CancellationToken ct)
    {
        const string label = "Anthropic API accepts the key";
        // A profile pointed at a proxy (ANTHROPIC_BASE_URL) is tested there, as Claude would use it.
        var raw = entry.Env.TryGetValue("ANTHROPIC_BASE_URL", out var b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : DefaultAnthropicBaseUrl;
        if (!Uri.TryCreate(raw.EndsWith('/') ? raw : raw + "/", UriKind.Absolute, out var baseUrl)
            || !(baseUrl.Scheme == Uri.UriSchemeHttps || (baseUrl.Scheme == Uri.UriSchemeHttp && baseUrl.IsLoopback)))
            return new CheckItem(CheckStatus.Warn, "Couldn't test the key",
                "ANTHROPIC_BASE_URL isn't an https address, so the key wasn't sent to it.") { Key = "live" };
        var (valid, error) = await keyTester.TestAsync(key, baseUrl, ct).ConfigureAwait(false);
        var detail = error == null ? null : Secrets.Scrub(error, key);
        return valid switch
        {
            true => new CheckItem(CheckStatus.Ok, label, null) { Key = "live" },
            false => new CheckItem(CheckStatus.Fail, "The Anthropic API rejected the key",
                $"Create a new key in the Claude Console, then run '{setCmd}'.", detail) { Key = "live" },
            _ => new CheckItem(CheckStatus.Warn, "Couldn't reach the Anthropic API to test the key", null, detail) { Key = "live" },
        };
    }
}
