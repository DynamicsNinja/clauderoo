using System.Diagnostics;
using Clauderoo.Core;

namespace Clauderoo.Cli;

/// <summary>Seams for the version, upgrade and uninstall commands. Anything left null uses the real thing.</summary>
public sealed class ReleaseHooks
{
    public string? CurrentVersion { get; set; }
    /// <summary>The GitHub owner/repo; "" means "not built from a release". Null uses the stamped value.</summary>
    public string? Repo { get; set; }
    public HttpMessageHandler? Http { get; set; }
    /// <summary>Runs the extracted clauderoo.exe's install and returns its exit code.</summary>
    public Func<string, int>? RunInstall { get; set; }
    public IEnvStore? Env { get; set; }
    public string? InstallDir { get; set; }
    public UninstallOptions? Uninstall { get; set; }
    public Func<string?>? ReadLine { get; set; }
    /// <summary>Seams for `doctor`.</summary>
    public DoctorOptions? Doctor { get; set; }
    /// <summary>Runs schtasks.exe for `auto --schedule`.</summary>
    public ITaskScheduler? Scheduler { get; set; }
    /// <summary>The tray exe the scheduled task runs (default: the installed clauderoow.exe, else the one next to cr).</summary>
    public string? TrayExe { get; set; }
}

internal static class ReleaseCommands
{
    public static int Version(TextWriter output, ReleaseHooks? hooks)
    {
        output.WriteLine(hooks?.CurrentVersion ?? AppVersion.Current);
        return 0;
    }

    public static int Upgrade(List<string> pos, TextWriter output, TextWriter err, ReleaseHooks? hooks)
    {
        var check = pos.Remove("--check");
        if (pos.Count > 0) throw new ClauderooException("Usage: clauderoo upgrade [--check]");

        var repo = hooks?.Repo ?? AppVersion.Repo;
        if (string.IsNullOrWhiteSpace(repo))
        {
            err.WriteLine("This build wasn't made from a GitHub release; pull and rerun install.");
            return 1;
        }

        var currentText = hooks?.CurrentVersion ?? AppVersion.Current;
        var current = AppVersion.Parse(currentText) ?? new Version(0, 0);
        var ownHandler = hooks?.Http == null ? new SocketsHttpHandler() : null;
        try
        {
            return UpgradeWith(ownHandler ?? hooks!.Http!, repo, check, currentText, current, output, err, hooks);
        }
        finally { ownHandler?.Dispose(); }
    }

    private static int UpgradeWith(HttpMessageHandler handler, string repo, bool check, string currentText, Version current,
        TextWriter output, TextWriter err, ReleaseHooks? hooks)
    {
        var updater = new Updater(handler, repo);

        ReleaseLookup lookup;
        try { lookup = updater.LookupAsync(default).GetAwaiter().GetResult(); }
        catch (HttpRequestException) { throw new ClauderooException("Couldn't reach GitHub to look for updates."); }
        catch (TaskCanceledException) { throw new ClauderooException("Couldn't reach GitHub to look for updates."); }

        var latest = lookup.Info;
        if (latest == null)
        {
            output.WriteLine(lookup.ReleaseExists
                ? $"The latest release ({lookup.Tag}) has no clauderoo-*-win-x64.zip / .sha256 assets."
                : "No release found.");
            return 0;
        }
        if (latest.Version <= current)
        {
            output.WriteLine($"clauderoo {currentText} is up to date.");
            return 0;
        }
        if (check)
        {
            output.WriteLine($"Update available: {currentText} -> {latest.Version} ({latest.Tag}). Run 'cr upgrade' to install it.");
            return 0;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "clauderoo-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            output.WriteLine($"Downloading {latest.Tag}...");
            string extracted;
            try { extracted = updater.DownloadVerifiedAsync(latest, tmp, default).GetAwaiter().GetResult(); }
            catch (ClauderooException) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                throw new ClauderooException("Couldn't download the update; nothing was changed.");
            }
            var exe = Path.Combine(extracted, "clauderoo.exe");
            if (!File.Exists(exe)) throw new ClauderooException("The download didn't contain clauderoo.exe; nothing was changed.");
            output.WriteLine("Verified. Installing...");
            var code = (hooks?.RunInstall ?? RunInstallProcess)(exe);
            if (code == 0) output.WriteLine($"Upgraded to {latest.Version}. Open a new terminal to use it. If the tray app is running, exit it and start it again (cr tray).");
            else err.WriteLine("The installer didn't finish; the previous version is still in place.");
            return code;
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static int RunInstallProcess(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, "install") { UseShellExecute = false })
            ?? throw new ClauderooException("Could not start the new installer.");
        p.WaitForExit();
        return p.ExitCode;
    }

    public static int Uninstall(List<string> pos, TextWriter output, ClauderooPaths paths, SessionTracker sessions, ReleaseHooks? hooks,
        Func<string?>? homeDir = null)
    {
        var yes = pos.Remove("--yes");
        var purge = pos.Remove("--purge");
        if (pos.Count > 0) throw new ClauderooException("Usage: clauderoo uninstall [--yes] [--purge]");

        var env = hooks?.Env ?? new WindowsEnvStore();
        var installDir = hooks?.InstallDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clauderoo");
        var data = hooks?.Uninstall?.DataDir ?? paths.Root;

        if (purge)
        {
            // Everything is checked and confirmed before anything changes.
            string? home = null;
            try { home = homeDir?.Invoke(); }
            catch (ClauderooException) { /* no or unreadable config: ~/.claude is still protected below */ }
            var keep = new[] { paths.DefaultHomeDir, home };
            Uninstaller.AssertSafeToPurge(data, keep);
            if (sessions.Running().Count > 0)
                throw new ClauderooException("Claude sessions started by clauderoo are still running. Close them, then run uninstall --purge again. Nothing was changed.");
            if (!yes)
            {
                output.WriteLine($"--purge permanently deletes {data}:");
                output.WriteLine("  every profile's sign-in, saved API keys and tokens, settings, history and removed profiles.");
                output.WriteLine($"  Your home profile ({home ?? paths.DefaultHomeDir}) is not touched.");
                output.Write("Type 'delete' to confirm: ");
                var answer = (hooks?.ReadLine ?? Console.In.ReadLine)()?.Trim();
                if (!string.Equals(answer, "delete", StringComparison.Ordinal))
                {
                    output.WriteLine("Not confirmed. Nothing was changed.");
                    return 1;
                }
            }
            var user = env.GetUser("CLAUDE_CONFIG_DIR");
            var clear = !string.IsNullOrEmpty(user) && (Path.GetFullPath(user).TrimEnd('\\', '/') + "\\")
                .StartsWith(Path.GetFullPath(data).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
            Uninstaller.Run(env, clear, installDir, hooks?.Uninstall);
            output.WriteLine($"Uninstalled: autostart is off, {installDir} is off your PATH and will be removed in a few seconds.");
            if (clear) output.WriteLine("The default profile setting (CLAUDE_CONFIG_DIR) was cleared.");
            Uninstaller.Purge(data, keep);
            output.WriteLine($"Deleted {data}. Your home profile was not touched.");
            return 0;
        }

        var clearDefault = yes;
        if (!yes && !string.IsNullOrEmpty(env.GetUser("CLAUDE_CONFIG_DIR")))
        {
            output.Write("Also clear the default profile (the user CLAUDE_CONFIG_DIR variable)? [y/N] ");
            var answer = (hooks?.ReadLine ?? Console.In.ReadLine)()?.Trim();
            clearDefault = answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        Uninstaller.Run(env, clearDefault, installDir, hooks?.Uninstall);

        output.WriteLine($"Uninstalled: autostart is off, {installDir} is off your PATH and will be removed in a few seconds.");
        if (sessions.Running().Count > 0)
            output.WriteLine($"Close running Claude sessions started by clauderoo; if {installDir} is still there afterwards, delete it.");
        if (clearDefault) output.WriteLine("The default profile setting (CLAUDE_CONFIG_DIR) was cleared.");
        output.WriteLine($"Your profiles, logins and settings were kept in {data}.");
        output.WriteLine("Delete that folder only if you're done with clauderoo. Use Explorer or 'rd /s' on it, not PowerShell 5.1 'Remove-Item -Recurse', which can follow junctions into your shared Claude folders.");
        return 0;
    }
}
