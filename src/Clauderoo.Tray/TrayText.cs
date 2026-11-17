using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>The words the tray uses (menu labels, tooltip, balloons). Pure: no UI, no I/O.</summary>
internal static class TrayText
{
    public const string ClaudeLogin = "Claude login";
    public const string Vertex = "Google Vertex AI";
    public const string ApiKey = "API key";
    public const string NotSignedIn = "not signed in";

    /// <summary>NotifyIcon.Text limit.</summary>
    public const int TooltipMax = 127;

    public static string ProviderName(string provider) => provider switch
    {
        Providers.Vertex => Vertex,
        Providers.ApiKey => ApiKey,
        _ => ClaudeLogin,
    };

    /// <summary>The account part: email (or "not signed in") for a Claude login, the project for Vertex, nothing for an API key.</summary>
    public static string? Account(ProfileInfo p) => p.Provider switch
    {
        Providers.Vertex => string.IsNullOrWhiteSpace(p.ProviderDetail) ? null : p.ProviderDetail,
        Providers.ApiKey => null,
        _ => string.IsNullOrWhiteSpace(p.Email) ? NotSignedIn : p.Email,
    };

    /// <summary>"Claude login · someone@example.com", "Google Vertex AI · my-proj", "API key".</summary>
    public static string Describe(ProfileInfo p) =>
        Account(p) is { } a ? $"{ProviderName(p.Provider)} · {a}" : ProviderName(p.Provider);

    /// <summary>Menu label (not yet &amp;-escaped): "work — Google Vertex AI · my-proj", "personal (home) — Claude login · …".</summary>
    public static string ProfileLabel(ProfileInfo p) =>
        p.Name + (p.IsHome ? " (home)" : "") + " — " + Describe(p);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static string Problems(int n) => Plural(n, "problem", "problems");

    public static string Running(int n) => Plural(n, "session running", "sessions running");

    public static string Header(int problems) =>
        problems == 0 ? "clauderoo — all good" : $"clauderoo — {Problems(problems)}";

    /// <summary>Error if a problem can't be fixed from the tray, Warning if any problem, Running if sessions run.</summary>
    public static AppIconState IconState(IReadOnlyCollection<Problem> problems, int running) =>
        problems.Any(IsSevere) ? AppIconState.Error
        : problems.Count > 0 ? AppIconState.Warning
        : running > 0 ? AppIconState.Running
        : AppIconState.Normal;

    /// <summary>A problem clauderoo can't fix itself (damaged file, a file in the way, unknown).</summary>
    public static bool IsSevere(Problem p) => p.Fix == null;

    public static string FixLabel(ProblemFix fix) => fix switch
    {
        ProblemFix.ShareAgain => "&Share it again",
        ProblemFix.RecreateLink => "&Repair the link",
        _ => "&Sync again",
    };

    /// <summary>
    /// "clauderoo — double-click: start work (Google Vertex AI, my-proj) · 2 problems · 1 session running", cut to
    /// <see cref="TooltipMax"/> characters (the account is shortened first).
    /// </summary>
    public static string Tooltip(ProfileInfo? def, int problems, int running)
    {
        var tail = " · " + (problems == 0 ? "no problems" : Problems(problems))
                   + (running > 0 ? " · " + Running(running) : "");
        if (def == null) return Cut("clauderoo — no default profile" + tail);

        var head = $"clauderoo — double-click: start {def.Name} (";
        var provider = ProviderName(def.Provider);
        var account = Account(def);
        var full = head + provider + (account != null ? ", " + account : "") + ")" + tail;
        if (full.Length <= TooltipMax || account == null) return Cut(full);

        // Too long: shorten the account (an email or project id) to what fits.
        var room = TooltipMax - (head.Length + provider.Length + 2 + 1 + tail.Length) - 1;
        return Cut(room >= 4
            ? head + provider + ", " + account[..room] + "…)" + tail
            : head + provider + ")" + tail);
    }

    /// <summary>The fallback-menu headline for a config that can't be loaded.</summary>
    public static (string Headline, string Detail) ConfigProblem(bool configExists) => configExists
        ? ("clauderoo's settings file is damaged", "Profiles can't be shown until it's repaired.")
        : ("clauderoo isn't set up yet", "Set it up to start sessions from here.");

    public static string Cut(string s, int max = TooltipMax) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>One balloon for a sync's notices.</summary>
    public static string Notices(IReadOnlyList<string> notices) =>
        notices.Count == 1 ? Cut(notices[0], 250) : Cut($"{notices.Count} updates: " + string.Join(" · ", notices), 250);

    /// <summary>The balloon for a set of problems that has new ones.</summary>
    public static string ProblemBalloon(IReadOnlyList<Problem> current) =>
        Cut((current.Count == 1 ? current[0].Title + "." : $"{Problems(current.Count)} need attention.")
            + " Click to see " + (current.Count == 1 ? "details." : "them."), 250);
}
