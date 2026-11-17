using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Clauderoo.Core;

/// <summary>One usage window: "5h", "7d" or a model's display name, with its use in percent (0-100).</summary>
public record UsageWindow(string Name, double Percent, DateTimeOffset? ResetsAt);

/// <summary>A profile's usage at one moment. Never holds a token.</summary>
public record UsageReading(string Profile, DateTimeOffset FetchedAt, string? Plan, IReadOnlyList<UsageWindow> Windows);

public abstract record UsageResult
{
    private UsageResult() { }

    public sealed record Ok(UsageReading Reading) : UsageResult;
    /// <summary>The sign-in has expired; starting a session of the profile refreshes it.</summary>
    public sealed record Expired : UsageResult;
    public sealed record NotSignedIn : UsageResult;
    /// <summary>The provider (Vertex, API key, token) has no subscription limits to report.</summary>
    public sealed record NotTracked(string Provider) : UsageResult;
    public sealed record RateLimited(TimeSpan RetryAfter) : UsageResult;
    /// <summary>A plain message, already scrubbed of the token.</summary>
    public sealed record Failed(string Message) : UsageResult;
    /// <summary>The (undocumented) endpoint answered with something that couldn't be parsed.</summary>
    public sealed record Unavailable : UsageResult;
}

/// <summary>
/// Fetches usage limits for Claude-login profiles from the OAuth usage endpoint. It reads the token through
/// <see cref="CredentialPeek"/>, never refreshes it, and scrubs it from every message it returns.
/// </summary>
public sealed class UsageClient(HttpMessageHandler handler, Func<DateTimeOffset> now, string version)
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(10);
    private const int MaxBodyInMessage = 300;

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<UsageResult> FetchAsync(string profile, string configDir, ProfileEntry entry, CancellationToken ct)
    {
        var provider = entry.Home ? Providers.Login : Providers.Of(entry);
        if (provider != Providers.Login) return new UsageResult.NotTracked(provider);

        var (peek, token) = CredentialPeek.Read(configDir, now());
        switch (peek.Status)
        {
            case PeekStatus.NotSignedIn: return new UsageResult.NotSignedIn();
            case PeekStatus.Expired: return new UsageResult.Expired();
            case PeekStatus.Unreadable: return new UsageResult.Failed("Couldn't read this profile's sign-in file.");
        }
        if (string.IsNullOrEmpty(token)) return new UsageResult.NotSignedIn();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", $"clauderoo/{version}");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return new UsageResult.Expired();
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return new UsageResult.RateLimited(RetryAfterOf(resp.Headers.RetryAfter));

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new UsageResult.Failed(Clean(
                    $"The usage service answered HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}", token));

            var windows = Parse(body);
            return windows == null
                ? new UsageResult.Unavailable()
                : new UsageResult.Ok(new UsageReading(profile, now(), peek.Plan, windows));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new UsageResult.Failed("The usage service didn't answer in time."); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return new UsageResult.Failed(Clean($"Couldn't reach the usage service: {e.GetType().Name}: {e.Message}", token));
        }
    }

    // Scrubs before truncating, so a cut can never leave part of the token behind.
    private static string Clean(string text, string token)
    {
        var s = Secrets.Scrub(text, token).ReplaceLineEndings(" ").Trim();
        return s.Length > MaxBodyInMessage ? s[..MaxBodyInMessage] + "…" : s;
    }

    private TimeSpan RetryAfterOf(RetryConditionHeaderValue? h)
    {
        if (h?.Delta is { } d && d > TimeSpan.Zero) return d;
        if (h?.Date is { } at && at > now()) return at - now();
        return DefaultRetryAfter;
    }

    /// <summary>The windows in the body, or null when it isn't the expected JSON object. Unknown fields are ignored.</summary>
    internal static List<UsageWindow>? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var list = new List<UsageWindow>();
            AddWindow(list, root, "five_hour", "5h");
            AddWindow(list, root, "seven_day", "7d");

            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                foreach (var l in limits.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    if (!l.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) continue;
                    if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(model, "display_name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (Num(l, "percent") is not { } pct) continue;
                    list.Add(new UsageWindow(name, pct, Time(l, "resets_at")));
                }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static void AddWindow(List<UsageWindow> list, JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return;
        if (Num(w, "utilization") is not { } pct) return;
        list.Add(new UsageWindow(name, pct, Time(w, "resets_at")));
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement obj, string name) =>
        Str(obj, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t
            : null;
}
