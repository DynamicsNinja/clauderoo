using System.Diagnostics;

namespace Clauderoo.Core;

public record LaunchSpec(string FileName, IReadOnlyList<string> Args, string WorkingDir, string? ConfigDirEnv);

public static class Launcher
{
    public static LaunchSpec NewWindow(ClauderooConfig cfg, ClauderooPaths paths, string profile,
        string dir, IReadOnlyList<string> claudeArgs, bool wtAvailable)
    {
        var configDir = ConfigDirFor(cfg, paths, profile);
        foreach (var a in claudeArgs)
            // Without wt, the command line is re-parsed by `cmd /c start`, which mangles the quotes
            // .NET adds around the inner command, so args needing quotes can't be passed reliably.
            if (a.IndexOfAny(BadArgChars) >= 0 || (!wtAvailable && QuoteIfNeeded(a) != a))
                throw new ClauderooException("Argument contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead.");
        foreach (var p in new[] { configDir, dir })
            if (p is not null && p.IndexOfAny(BadPathChars) >= 0)
                throw new ClauderooException($"Path '{p}' contains characters that can't be passed through a new terminal window; use 'clauderoo run' instead.");
        var cmdLine = $"set CLAUDE_CONFIG_DIR={configDir}&& claude"
            + string.Concat(claudeArgs.Select(a => " " + QuoteIfNeeded(a)));
        var title = $"Claude · {profile}";
        string[] args = wtAvailable
            ? ["-w", "0", "new-tab", "--title", title, "-d", dir, "cmd", "/k", cmdLine]
            : ["/c", "start", title, "/D", dir, "cmd", "/k", cmdLine];
        return new LaunchSpec(wtAvailable ? "wt.exe" : "cmd.exe", args, dir, configDir);
    }

    public static LaunchSpec InPlace(ClauderooConfig cfg, ClauderooPaths paths, string profile,
        string dir, IReadOnlyList<string> claudeArgs) =>
        new(ResolveClaude(), claudeArgs, dir, ConfigDirFor(cfg, paths, profile));

    private static string? ConfigDirFor(ClauderooConfig cfg, ClauderooPaths paths, string profile)
    {
        if (!cfg.Profiles.TryGetValue(profile, out var entry))
            throw new ClauderooException($"Unknown profile '{profile}'.");
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

    public static Process Start(LaunchSpec s)
    {
        var psi = new ProcessStartInfo(s.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = s.WorkingDir,
        };
        foreach (var a in s.Args) psi.ArgumentList.Add(a);
        if (s.ConfigDirEnv is null) psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        else psi.Environment["CLAUDE_CONFIG_DIR"] = s.ConfigDirEnv;
        return Process.Start(psi) ?? throw new ClauderooException($"Failed to start {s.FileName}.");
    }
}
