using System.Diagnostics;

namespace Clauderoo.Core;

public record LaunchSpec(string FileName, IReadOnlyList<string> Args, string WorkingDir, string? ConfigDirEnv, string? Profile = null,
    IReadOnlyDictionary<string, string>? ExtraEnv = null, Func<string, bool>? DropEnv = null)
{
    // DropEnv: inherited variables it matches are removed before ConfigDirEnv and ExtraEnv are applied (a delegated
    // run's clean environment, see DelegateEnv). Null for every normal launch.
    /// <summary>ExtraEnv can carry a secret (CLAUDE_CODE_OAUTH_TOKEN), so only its names are ever printed.</summary>
    public override string ToString() =>
        $"LaunchSpec {{ FileName = {FileName}, Args = [{string.Join(", ", Args)}], WorkingDir = {WorkingDir}, " +
        $"ConfigDirEnv = {ConfigDirEnv}, Profile = {Profile}, " +
        $"ExtraEnv = [{string.Join(", ", (ExtraEnv?.Keys ?? []).Order(StringComparer.Ordinal))}] }}";
}

public static class Launcher
{
    /// <param name="onPath">Whether an exe is on PATH (the PowerShell shells check for theirs); null checks PATH.</param>
    public static LaunchSpec NewWindow(ClauderooConfig cfg, ClauderooPaths paths, string profile,
        string dir, IReadOnlyList<string> claudeArgs, bool wtAvailable, string? clauderooExe = null, bool isolated = false,
        Func<string, bool>? onPath = null)
    {
        // The new terminal runs `clauderoo run <profile>` rather than claude itself, so that process records the
        // claude session it starts (the terminal host's own pid says nothing about claude).
        clauderooExe ??= RequireHelper(HelperExePath.Resolve());
        var configDir = ConfigDirFor(cfg, paths, profile, isolated);
        // Terminal "conhost" forces a classic console window even when Windows Terminal is installed.
        wtAvailable = wtAvailable && TerminalChoice.TerminalOf(cfg) == TerminalChoice.Wt;
        var shell = TerminalChoice.ShellOf(cfg);
        if (TerminalChoice.IsPowerShell(shell))
            return PowerShellWindow(shell, profile, dir, configDir, clauderooExe, claudeArgs, isolated, wtAvailable,
                onPath ?? TerminalChoice.OnPath);
        foreach (var a in claudeArgs)
            // Without wt, the command line is re-parsed by `cmd /c start`, which mangles the quotes
            // .NET adds around the inner command, so args needing quotes can't be passed reliably.
            if (a.IndexOfAny(BadArgChars) >= 0 || (!wtAvailable && QuoteIfNeeded(a) != a))
                throw new ClauderooException("Argument contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead.");
        foreach (var p in new[] { configDir, dir, clauderooExe })
            if (p is not null && p.IndexOfAny(BadPathChars) >= 0)
                throw new ClauderooException($"Path '{p}' contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead.");
        // An existing Windows Terminal window may host the tab with its own environment, so the session markers a
        // calling session carried are cleared in the tab's shell too (StartInfo strips them from wt/cmd itself).
        var cmdLine = CmdCommandLine(configDir, clauderooExe, profile, isolated, claudeArgs);
        var title = $"Claude · {profile}";
        // Without wt, `cmd /c start` re-parses the line and does not understand the \" escapes .NET would put around a
        // single combined command, so the exe, `run`, the profile and the args are passed as separate arguments
        // (the exe path gets plain quotes, which `cmd /k` keeps for a path with spaces). CLAUDE_CONFIG_DIR travels
        // in the environment (LaunchSpec.ConfigDirEnv) and `run` sets it again itself.
        if (!wtAvailable && clauderooExe.IndexOfAny(['(', ')']) >= 0)
            throw new ClauderooException($"Path '{clauderooExe}' contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead.");
        string[] args = wtAvailable
            ? ["-w", "0", "new-tab", "--title", title, "-d", dir, "cmd", "/k", cmdLine]
            : ["/c", "start", title, "/D", dir, "cmd", "/k", clauderooExe, "run", profile,
                .. (isolated ? new[] { "--isolated" } : []),
                .. (claudeArgs.Count > 0 ? new[] { "--" }.Concat(claudeArgs) : [])];
        return new LaunchSpec(wtAvailable ? "wt.exe" : "cmd.exe", args, dir, configDir);
    }

    // cmd's line for `cmd /k`: sets the profile's config folder, clears the session markers and runs `clauderoo run`.
    private static string CmdCommandLine(string? configDir, string clauderooExe, string profile, bool isolated,
        IReadOnlyList<string> claudeArgs) =>
        $"set CLAUDE_CONFIG_DIR={configDir}&& set {WhichProfile.SessionVar}=&& set {IsolatedVar}=&& \"{clauderooExe}\" run {profile}"
        + (isolated ? " --isolated" : "")
        + (claudeArgs.Count > 0 ? " --" : "")
        + string.Concat(claudeArgs.Select(a => " " + QuoteIfNeeded(a)));

    /// <summary>Windows' command-line length limit, less room for the terminal's own options.</summary>
    private const int MaxCommandLine = 30000;

    // PowerShell gets its whole script as -EncodedCommand (base64 of UTF-16), which neither wt nor cmd re-parses: no
    // character in an argument or a path can break out of it, so cmd's restrictions (" % ! ; and friends) don't apply.
    // Inside the script every value is a single-quoted PowerShell literal, where nothing is expanded.
    private static LaunchSpec PowerShellWindow(string shell, string profile, string dir, string? configDir,
        string clauderooExe, IReadOnlyList<string> claudeArgs, bool isolated, bool wtAvailable, Func<string, bool> onPath)
    {
        var exe = TerminalChoice.ShellExe(shell);
        if (!onPath(exe))
            throw new ClauderooException(
                $"{TerminalChoice.ShellName(shell)} ({exe}) wasn't found on PATH. Install it, or go back to Command Prompt with 'cr config shell cmd'.");
        var shellArgs = PowerShellArgs(PowerShellScript(configDir, dir, clauderooExe, profile, isolated, claudeArgs));
        var title = $"Claude · {profile}";
        // wt splits its command line at ';' (even inside an argument), so a folder with one isn't given to -d; the
        // script's Set-Location takes the tab there anyway. Without wt, `cmd /c start` opens the console window (a
        // direct start would share the caller's console); the folder reaches it as cmd's working directory, never
        // through cmd's parser.
        string[] args = wtAvailable
            ? ["-w", "0", "new-tab", "--title", title, .. (dir.Contains(';') ? Array.Empty<string>() : ["-d", dir]), exe, .. shellArgs]
            : ["/c", "start", title, exe, .. shellArgs];
        if (args.Sum(a => a.Length + 3) > MaxCommandLine)
            throw new ClauderooException("The arguments are too long to pass to a new terminal window; use 'clauderoo run' instead.");
        return new LaunchSpec(wtAvailable ? "wt.exe" : "cmd.exe", args, dir, configDir);
    }

    internal static string[] PowerShellArgs(string script) =>
        ["-NoExit", "-NoLogo", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))];

    /// <summary>
    /// The script a PowerShell tab runs: the same steps as cmd's line. clauderoo is started with Start-Process and a
    /// command line built here (Windows quoting rules), because PowerShell's own native-argument passing differs
    /// between versions (5.1 mangles embedded quotes). It waits for that process only, not for anything it leaves behind.
    /// </summary>
    internal static string PowerShellScript(string? configDir, string? dir, string clauderooExe, string profile, bool isolated,
        IReadOnlyList<string> claudeArgs)
    {
        List<string> runArgs = ["run", profile];
        if (isolated) runArgs.Add("--isolated");
        if (claudeArgs.Count > 0) runArgs.AddRange(["--", .. claudeArgs]);
        var lines = new List<string>
        {
            // Assigning $null removes the variable, as cmd's `set NAME=` does.
            "$env:CLAUDE_CONFIG_DIR = " + (configDir is null ? "$null" : PsQuote(configDir)),
            $"$env:{WhichProfile.SessionVar} = $null",
            $"$env:{IsolatedVar} = $null",
        };
        if (dir is not null) lines.Add($"Set-Location -LiteralPath {PsQuote(dir)}");
        lines.Add($"(Start-Process -FilePath {PsQuote(clauderooExe)} -ArgumentList {PsQuote(WindowsCommandLine(runArgs))} -NoNewWindow -PassThru).WaitForExit()");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// A PowerShell single-quoted literal: nothing inside is expanded, and the only special characters are the quote
    /// itself and its typographic forms (PowerShell treats ‘ ’ ‚ ‛ as quotes too), each escaped by doubling.
    /// </summary>
    internal static string PsQuote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Arguments joined into one command line that CommandLineToArgvW (and .NET's argv) splits back exactly.</summary>
    internal static string WindowsCommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(WindowsQuote));

    private static string WindowsQuote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return a;
        var sb = new System.Text.StringBuilder(a.Length + 2).Append('"');
        var slashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { slashes++; continue; }
            // Backslashes before a quote are doubled and the quote escaped; elsewhere they are literal.
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString(); // doubled before the closing quote
    }

    /// <summary>
    /// The command line a Windows Terminal profile runs for <paramref name="profile"/>: the configured shell, which
    /// sets the profile's environment, runs `clauderoo run` and then stays open, as a new tab does.
    /// </summary>
    public static string ProfileCommandLine(ClauderooConfig cfg, ClauderooPaths paths, string profile, string clauderooExe)
    {
        var configDir = ConfigDirFor(cfg, paths, profile);
        var shell = TerminalChoice.ShellOf(cfg);
        if (TerminalChoice.IsPowerShell(shell))
            return WindowsCommandLine([TerminalChoice.ShellExe(shell),
                .. PowerShellArgs(PowerShellScript(configDir, null, clauderooExe, profile, false, []))]);
        foreach (var p in new[] { configDir, clauderooExe })
            if (p is not null && p.IndexOfAny(BadPathChars) >= 0)
                throw new ClauderooException($"Path '{p}' contains characters Command Prompt can't be given; use 'cr config shell pwsh'.");
        return "cmd.exe /k " + CmdCommandLine(configDir, clauderooExe, profile, false, []);
    }

    /// <summary>The helper the new terminal runs must exist, or the tab would just show "not recognized".</summary>
    internal static string RequireHelper(string exe) =>
        File.Exists(exe) ? exe : throw new ClauderooException("clauderoo isn't installed properly — reinstall it.");

    public static LaunchSpec InPlace(ClauderooConfig cfg, ClauderooPaths paths, string profile,
        string dir, IReadOnlyList<string> claudeArgs, string? claudePath = null, bool isolated = false)
    {
        var env = new Dictionary<string, string> { [WhichProfile.SessionVar] = profile };
        if (isolated) env[IsolatedVar] = "1";
        return new(claudePath ?? ResolveClaude(), claudeArgs, dir, ConfigDirFor(cfg, paths, profile, isolated), profile, env);
    }

    public const string IsolatedVar = "CLAUDEROO_ISOLATED";

    private static string? ConfigDirFor(ClauderooConfig cfg, ClauderooPaths paths, string profile, bool isolated = false)
    {
        if (!cfg.Profiles.TryGetValue(profile, out var entry))
            throw new ClauderooException($"Unknown profile '{profile}'.");
        if (isolated) return paths.IsolatedDir(profile);
        return entry.Home ? null : paths.ProfileDir(profile);
    }

    private static readonly char[] BadArgChars = ['"', '%', '!', ';'];
    private static readonly char[] BadPathChars = ['"', '%', '!', ';', '&', '|', '<', '>', '^'];
    private static readonly char[] QuoteChars = ['&', '|', '<', '>', '^', '(', ')'];

    private static string QuoteIfNeeded(string a) =>
        a.Any(char.IsWhiteSpace) || a.IndexOfAny(QuoteChars) >= 0 ? "\"" + a + "\"" : a;

    public static string ResolveClaude() =>
        ResolveClaude(Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD");

    public static string ResolveClaude(string path, string pathExt)
    {
        var exts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try
                {
                    var f = Path.Combine(d.Trim('"'), "claude" + ext);
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException) { }
            }
        throw new ClauderooException("'claude' was not found on PATH.");
    }

    public static bool WtAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(d.Trim('"'), "wt.exe"))) return true; }
            catch (ArgumentException) { }
        }
        return false;
    }

    /// <summary>Starts the process and records it as a running session of <paramref name="spec"/>.Profile.</summary>
    public static Process Start(LaunchSpec s, SessionTracker sessions)
    {
        var p = Start(s);
        if (s.Profile != null)
        {
            try { sessions.Record(s.Profile, p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* tracking is best effort */ }
        }
        return p;
    }

    public static Process Start(LaunchSpec s) =>
        Process.Start(StartInfo(s)) ?? throw new ClauderooException($"Failed to start {s.FileName}.");

    internal static ProcessStartInfo StartInfo(LaunchSpec s)
    {
        var psi = new ProcessStartInfo(s.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = s.WorkingDir,
        };
        foreach (var a in s.Args) psi.ArgumentList.Add(a);
        if (s.DropEnv is not null)
            foreach (var k in psi.Environment.Keys.Where(s.DropEnv).ToList()) psi.Environment.Remove(k);
        if (s.ConfigDirEnv is null) psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        else psi.Environment["CLAUDE_CONFIG_DIR"] = s.ConfigDirEnv;
        // A token inherited from the calling shell (e.g. cr run inside a token profile's session) would sign the
        // new session in as that account; only the token the spec injects for its own profile may pass. The same goes
        // for the session markers: a stale CLAUDEROO_PROFILE/CLAUDEROO_ISOLATED would make `which` name the caller.
        psi.Environment.Remove(ProfileManager.TokenEnvVar);
        psi.Environment.Remove(WhichProfile.SessionVar);
        psi.Environment.Remove(IsolatedVar);
        if (s.ExtraEnv is not null)
            foreach (var (k, v) in s.ExtraEnv) psi.Environment[k] = v;
        return psi;
    }
}
