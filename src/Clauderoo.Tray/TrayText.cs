using System.Text.RegularExpressions;
using Clauderoo.Core;

namespace Clauderoo.Tray;

/// <summary>Why the settings couldn't be loaded.</summary>
internal enum ConfigIssue
{
    /// <summary>No config.json yet: clauderoo was never set up.</summary>
    Missing,
    /// <summary>config.json exists but isn't valid.</summary>
    Damaged,
    /// <summary>config.json couldn't be read (locked, no access).</summary>
    Unreadable,
}

/// <summary>The words the tray uses (menu labels, tooltip, balloons, Problems window). Pure: no UI, no I/O.</summary>
internal static class TrayText
{
    public const string ClaudeLogin = "Claude login";
    public const string Vertex = "Google Vertex AI";
    public const string ApiKey = "API key";
    public const string Token = "Claude token";
    public const string NotSignedIn = "not signed in";

    /// <summary>NotifyIcon.Text limit.</summary>
    public const int TooltipMax = 127;

    /// <summary>Longest account (email) or project shown before it's cut with "…".</summary>
    public const int AccountMax = 40;

    public static string ProviderName(string provider) => provider switch
    {
        Providers.Vertex => Vertex,
        Providers.ApiKey => ApiKey,
        Providers.Token => Token,
        _ => ClaudeLogin,
    };

    /// <summary>
    /// The account part: email (or "not signed in") for a Claude login, the project for Vertex, nothing for an API key
    /// or a Claude token. Cut to <see cref="AccountMax"/> characters.
    /// </summary>
    public static string? Account(ProfileInfo p) => p.Provider switch
    {
        Providers.Vertex => string.IsNullOrWhiteSpace(p.ProviderDetail) ? null : Cut(p.ProviderDetail, AccountMax),
        Providers.ApiKey or Providers.Token => null,
        _ => string.IsNullOrWhiteSpace(p.Email) ? NotSignedIn : Cut(p.Email, AccountMax),
    };

    /// <summary>True for a Claude login with no account: the menu shows "not signed in" muted.</summary>
    public static bool IsSignedOut(ProfileInfo p) =>
        p.Provider is not (Providers.Vertex or Providers.ApiKey or Providers.Token) && string.IsNullOrWhiteSpace(p.Email);

    /// <summary>"Claude login · someone@example.com", "Google Vertex AI · my-proj", "API key".</summary>
    public static string Describe(ProfileInfo p) =>
        Account(p) is { } a ? $"{ProviderName(p.Provider)} · {a}" : ProviderName(p.Provider);

    /// <summary>Menu label (not yet &amp;-escaped): "work — Google Vertex AI · my-proj", "personal (home) — Claude login · …".</summary>
    public static string ProfileLabel(ProfileInfo p) =>
        p.Name + (p.IsHome ? " (home)" : "") + " — " + Describe(p);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static string Problems(int n) => Plural(n, "problem", "problems");

    public static string Running(int n) => Plural(n, "session running", "sessions running");

    /// <summary>The menu's first line: "clauderoo — all good", or the clickable "3 problems…" (with a mnemonic).</summary>
    public static string Header(int problems) =>
        problems == 0 ? "clauderoo — all good" : $"{problems} &{(problems == 1 ? "problem" : "problems")}…";

    /// <summary>Error if a problem can't be fixed from the tray, Warning if any problem, Running if sessions run.</summary>
    public static AppIconState IconState(IReadOnlyCollection<Problem> problems, int running) =>
        problems.Any(IsSevere) ? AppIconState.Error
        : problems.Count > 0 ? AppIconState.Warning
        : running > 0 ? AppIconState.Running
        : AppIconState.Normal;

    /// <summary>A problem clauderoo can't fix itself (damaged file, a file in the way, unknown).</summary>
    public static bool IsSevere(Problem p) => p.Fix == null;

    /// <summary>What identifies a problem for "is this new?": the same profile and plain title.</summary>
    public static string Key(Problem p) => p.Profile + "\n" + p.Title;

    public static string FixLabel(ProblemFix fix) => fix switch
    {
        ProblemFix.ShareAgain => "&Share it again",
        ProblemFix.RecreateLink => "&Repair the link",
        _ => "&Sync again",
    };

    static readonly Regex ItemPattern = new(@"^[^:\s]+: (?<d>\S+) is ", RegexOptions.Compiled);

    /// <summary>One plain line: what the Fix button will do, or what to do when there's no fix.</summary>
    public static string Explain(Problem p)
    {
        var who = p.Profile.Length > 0 ? p.Profile : "this profile";
        var item = ItemPattern.Match(p.Detail) is { Success: true } m ? m.Groups["d"].Value : "shared";
        return p.Fix switch
        {
            ProblemFix.ShareAgain =>
                $"Share it again replaces {who}'s own {item} folder with the shared one; clauderoo keeps a backup of the old folder.",
            ProblemFix.RecreateLink =>
                $"Repair the link points {who}'s {item} folder back at the shared one. Nothing in the shared folder is changed.",
            ProblemFix.Resync =>
                $"Sync again retries the update for {who}. If another program is using Claude's files, close it first.",
            _ when p.Title.Contains("damaged") =>
                "A Claude settings file has a mistake in it, so clauderoo left it alone. Copy the details and send them to whoever helps you with clauderoo.",
            _ when p.Title.StartsWith("A file is blocking") =>
                $"Something put a file where {who}'s shared folder should be. Copy the details and send them to whoever helps you with clauderoo.",
            _ => "clauderoo can't fix this one by itself. Copy the details and send them to whoever helps you with clauderoo.",
        };
    }

    /// <summary>
    /// Three lines within <see cref="TooltipMax"/>:
    /// "clauderoo — double-click starts work" / "Google Vertex AI · my-proj" / "⚠ 2 problems · ● 1 session running".
    /// </summary>
    public static string Tooltip(ProfileInfo? def, int problems, int running)
    {
        var status = (problems == 0 ? "No problems" : "⚠ " + Problems(problems))
                     + (running > 0 ? " · ● " + Running(running) : "");
        if (def == null) return Cut("clauderoo — no default profile\n" + status);

        var line2 = Describe(def);
        var line1 = "clauderoo — double-click starts " + def.Name;
        // Fit: shorten the provider line first, then the name line.
        var room = TooltipMax - status.Length - 2;
        if (line1.Length + line2.Length > room)
            line2 = Cut(line2, Math.Max(1, room - line1.Length));
        if (line1.Length + line2.Length > room)
            line1 = Cut(line1, Math.Max(1, room - line2.Length));
        return Cut(line1 + "\n" + line2 + "\n" + status);
    }

    /// <summary>The fallback menu's headline and plain detail for a config that can't be loaded.</summary>
    public static (string Headline, string Detail) ConfigProblem(ConfigIssue issue, string? message = null) => issue switch
    {
        ConfigIssue.Missing => ("clauderoo isn't set up yet", "Set it up to start sessions from here."),
        ConfigIssue.Damaged => ("clauderoo's settings file is damaged", Cut(message ?? "Its settings file can't be understood.", 90)),
        _ => ("clauderoo's settings can't be read right now", "Another program may be using the file. Retry in a moment."),
    };

    /// <summary>Plain words for <see cref="ProfileManager.Init"/>'s failures.</summary>
    public static string SetUpFailed(string message) =>
        message.Contains("CLAUDE_CONFIG_DIR")
            ? "Claude is already set to use another folder — ask for help setting up clauderoo."
            : message.StartsWith("Home folder not found")
                ? "Claude hasn't been used on this PC yet — start Claude once, then choose Set up."
                : message;

    public static string Cut(string s, int max = TooltipMax) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary><paramref name="text"/> with a mnemonic (&amp;) before the first <paramref name="at"/>: ("Claude login", "login") → "Claude &amp;login".</summary>
    public static string AccessKey(string text, string at)
    {
        var i = text.IndexOf(at, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + "&" + text[i..];
    }

    public const string SomethingWentWrong = "Something went wrong — see Problems for details.";

    /// <summary>
    /// Plain words for an error from something started in the tray. Core and the launcher speak to the command line
    /// ("use 'clauderoo run' instead"), so known errors are reworded; <c>Unknown</c> is true for text not meant for
    /// people, which is replaced by <see cref="SomethingWentWrong"/> (the raw text goes to the Problems window).
    /// </summary>
    public static (string Text, bool Unknown) PlainError(Exception e)
    {
        var m = e.Message;
        if (m.Contains("can't be passed through a new terminal window"))
            return ("This session can't be opened in a new tab: a folder it uses has characters like % ; or ! in its name. Choose a different start folder.", false);
        if (m.StartsWith("Unknown profile '", StringComparison.Ordinal))
            return ("That profile doesn't exist any more.", false);
        if (e is ClauderooException && !LooksLikeCli(m))
            return (m, false);
        return (SomethingWentWrong, true);
    }

    static bool LooksLikeCli(string m) =>
        m.Contains("'clauderoo ") || m.Contains("'cr ") || m.Contains("--") || m.Contains("clauderoo.exe");

    /// <summary>A line for the Problems window's "Recent messages" list about an error the balloon only summarised.</summary>
    public static string RecentError(string raw) => "Something went wrong: " + raw;

    /// <summary>One balloon for a sync's notices.</summary>
    public static string Notices(IReadOnlyList<string> notices) =>
        notices.Count == 1 ? Cut(notices[0], 250) : Cut($"{notices.Count} updates: " + string.Join(" · ", notices), 250);

    /// <summary>The menu item that upgrades clauderoo ("Update to v1.2.0").</summary>
    public static string UpdateItem(string version) => $"&Update to v{version}";

    /// <summary>The once-per-release balloon about a newer version.</summary>
    public static string UpdateBalloon(string version) =>
        $"clauderoo v{version} is available. Open the tray menu and choose Update to v{version}.";

    /// <summary>
    /// The message after an import in the window: what was created, updated and mapped, which variables arrived (names only),
    /// what was skipped or kept, what still needs a secret, and any notices.
    /// </summary>
    public static string ImportSummary(ImportResult r)
    {
        var lines = new List<string>();
        void Add(string heading, IReadOnlyList<string>? items, Func<string, string>? map = null)
        {
            if (items is not { Count: > 0 }) return;
            lines.Add(heading);
            lines.AddRange(items.Select(i => "  " + (map?.Invoke(i) ?? i)));
        }

        if (!r.ChangedAnything) lines.Add("Nothing was imported.");
        Add("Created:", r.Created);
        Add("Updated:", r.Updated);
        Add("Folder mappings added:", r.Mapped);
        Add("Environment variables (names only):", r.EnvNames);
        Add("Skipped:", r.Skipped, s => s.Replace("(use --force to update it)", "(answer Yes to update existing profiles)"));
        Add("Kept their sign-in:", r.KeptProvider);
        Add("Needs a key or token:", r.NeedSecret);
        Add("Notices:", r.Notices);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The balloon for a set of problems that has new ones.</summary>
    public static string ProblemBalloon(IReadOnlyList<Problem> current) =>
        Cut((current.Count == 1 ? current[0].Title + "." : $"{Problems(current.Count)} need attention.")
            + " Click to see " + (current.Count == 1 ? "details." : "them."), 250);
}
