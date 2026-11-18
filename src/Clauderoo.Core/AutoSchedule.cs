using System.Diagnostics;

namespace Clauderoo.Core;

/// <summary>Runs schtasks.exe (tests fake it).</summary>
public interface ITaskScheduler
{
    (int exit, string output) Run(IReadOnlyList<string> args);
}

public sealed class SchtasksScheduler : ITaskScheduler
{
    public (int exit, string output) Run(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new ClauderooException("Couldn't start schtasks.exe.");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(); } catch (Exception k) when (k is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return (-1, "schtasks.exe didn't finish within 30 seconds");
            }
            return (p.ExitCode, (o.Result + e.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) { return (-1, "Couldn't start schtasks.exe: " + ex.Message); }
    }
}

/// <summary>
/// The per-user Task Scheduler task that runs auto-switch once every few minutes while the tray isn't running. It is
/// created for the current user only (schtasks' default: runs while you're signed in, no stored password, no
/// elevation). It runs the tray exe with --auto-once, which runs `clauderoo auto --once` without a console window.
/// </summary>
public static class AutoSchedule
{
    public const string TaskName = "clauderoo auto-switch";
    public const string TrayArgument = "--auto-once";
    public const int DefaultMinutes = 10;

    public static IReadOnlyList<string> CreateArgs(string trayExe, int minutes) =>
        ["/Create", "/TN", TaskName, "/TR", $"\"{trayExe}\" {TrayArgument}", "/SC", "MINUTE", "/MO",
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), "/F"];

    public static IReadOnlyList<string> QueryArgs() => ["/Query", "/TN", TaskName];
    public static IReadOnlyList<string> DeleteArgs() => ["/Delete", "/TN", TaskName, "/F"];

    public static void Enable(ITaskScheduler s, string trayExe, int minutes)
    {
        if (minutes is < 1 or > 1439) throw new ClauderooException("--every must be a whole number of minutes from 1 to 1439.");
        if (trayExe.Contains('"')) throw new ClauderooException($"Can't schedule {trayExe}: the path contains a quote.");
        var (exit, output) = s.Run(CreateArgs(trayExe, minutes));
        if (exit != 0) throw new ClauderooException($"Couldn't create the scheduled task: {Last(output)}");
    }

    /// <returns>False when there was no task to remove.</returns>
    public static bool Disable(ITaskScheduler s)
    {
        if (s.Run(QueryArgs()).exit != 0) return false;
        var (exit, output) = s.Run(DeleteArgs());
        if (exit != 0) throw new ClauderooException($"Couldn't remove the scheduled task: {Last(output)}");
        return true;
    }

    private static string Last(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(line) ? "schtasks.exe failed" : line;
    }
}
