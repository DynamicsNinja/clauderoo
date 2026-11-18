namespace Clauderoo.Core;

/// <summary>
/// Where new sessions open (<see cref="ClauderooConfig.Terminal"/>) and which shell hosts them
/// (<see cref="ClauderooConfig.Shell"/>). An unknown value in a hand-edited config falls back to the default.
/// </summary>
public static class TerminalChoice
{
    /// <summary>Windows Terminal; falls back to a console window when wt.exe isn't on PATH.</summary>
    public const string Wt = "wt";
    /// <summary>Always a classic console window.</summary>
    public const string Conhost = "conhost";

    public const string Cmd = "cmd";
    /// <summary>PowerShell 7 (pwsh.exe).</summary>
    public const string Pwsh = "pwsh";
    /// <summary>Windows PowerShell 5.1 (powershell.exe).</summary>
    public const string PowerShell = "powershell";

    public static IReadOnlyList<string> Terminals { get; } = [Wt, Conhost];
    public static IReadOnlyList<string> Shells { get; } = [Cmd, Pwsh, PowerShell];

    public static string TerminalOf(ClauderooConfig cfg) => Terminals.Contains(cfg.Terminal) ? cfg.Terminal : Wt;
    public static string ShellOf(ClauderooConfig cfg) => cfg.Shell is { } s && Shells.Contains(s) ? s : Cmd;

    public static bool IsPowerShell(string shell) => shell is Pwsh or PowerShell;

    /// <summary>The shell's exe, as found on PATH.</summary>
    public static string ShellExe(string shell) => shell switch
    {
        Pwsh => "pwsh.exe",
        PowerShell => "powershell.exe",
        _ => "cmd.exe",
    };

    public static string TerminalName(string terminal) => terminal == Conhost ? "a console window" : "Windows Terminal";

    public static string ShellName(string shell) => shell switch
    {
        Pwsh => "PowerShell 7",
        PowerShell => "Windows PowerShell",
        _ => "Command Prompt",
    };

    /// <summary>True when <paramref name="exe"/> is in one of the PATH folders.</summary>
    public static bool OnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(d.Trim('"'), exe))) return true; }
            catch (ArgumentException) { }
        }
        return false;
    }
}
