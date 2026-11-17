using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public enum PeekStatus { Ok, NotSignedIn, Unreadable, Expired }

/// <summary>What clauderoo may know about a profile's Claude sign-in. Carries no token.</summary>
public readonly record struct Peek(PeekStatus Status, string? Plan, DateTimeOffset? ExpiresAt);

/// <summary>
/// The only code that opens a profile's <c>.credentials.json</c>. It reads the file read-only, in memory, and never
/// writes, copies, caches or logs it. The access token is handed back only to internal callers (the usage call) and
/// is never put in a public type, a message or an exception.
/// </summary>
public static class CredentialPeek
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);
    private static readonly Regex MaxTier = new(@"_max_(\d+)x$", RegexOptions.CultureInvariant);

    internal static (Peek Peek, string? Token) Read(string configDir, DateTimeOffset now)
    {
        var file = Path.Combine(configDir, ".credentials.json");
        if (!File.Exists(file)) return (new Peek(PeekStatus.NotSignedIn, null, null), null);
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
                return (new Peek(PeekStatus.NotSignedIn, null, null), null);

            var token = Str(oauth, "accessToken");
            if (string.IsNullOrEmpty(token)) return (new Peek(PeekStatus.NotSignedIn, null, null), null);

            var plan = PlanOf(Str(oauth, "subscriptionType"), Str(oauth, "rateLimitTier"));
            DateTimeOffset? expires = null;
            if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms))
            {
                try { expires = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
                catch (ArgumentOutOfRangeException) { }
            }

            if (expires is { } x && x <= now + ExpiryMargin)
                return (new Peek(PeekStatus.Expired, plan, expires), null);
            return (new Peek(PeekStatus.Ok, plan, expires), token);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The exception text is dropped on purpose: a JSON error can quote part of the file.
            return (new Peek(PeekStatus.Unreadable, null, null), null);
        }
    }

    /// <summary>"max" + "default_claude_max_5x" gives "Max 5x"; "pro" gives "Pro".</summary>
    internal static string? PlanOf(string? subscription, string? tier)
    {
        if (string.IsNullOrWhiteSpace(subscription)) return null;
        var s = subscription.Trim();
        var plan = char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
        if (tier != null && MaxTier.Match(tier) is { Success: true } m) plan += $" {m.Groups[1].Value}x";
        return plan;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
