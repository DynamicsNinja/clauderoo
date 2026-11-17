using System.Diagnostics;

namespace Clauderoo.Core;

/// <summary>Seams for tests and the CLI. Every member defaults to the real thing.</summary>
public sealed class UninstallOptions
{
    public IUserPath? Path { get; set; }
    public Action<bool>? SetAutoStart { get; set; }
    /// <summary>Starts the delayed removal; receives the arguments for cmd.exe.</summary>
    public Action<string>? Schedule { get; set; }
    /// <summary>The per-user data folder that must never be touched (default %USERPROFILE%\.clauderoo).</summary>
    public string? DataDir { get; set; }
    /// <summary>True when the tray app (clauderoow.exe) is running (default: checks the process list).</summary>
    public Func<bool>? IsTrayRunning { get; set; }
}

public static class Uninstaller
{
    private const string ConfigDirVar = "CLAUDE_CONFIG_DIR";

    /// <summary>The cmd.exe arguments that remove the install folder shortly after this process is gone.</summary>
    public static string BuildRemoveArguments(string installDir) => $"/c timeout 3 & rd /s /q \"{installDir}\"";

    /// <summary>
    /// Turns off autostart, removes installDir from the raw user PATH, optionally clears the user CLAUDE_CONFIG_DIR,
    /// moves exes aside and schedules removal of installDir. It never touches the .clauderoo data folder, and refuses
    /// (before changing anything) a folder that holds a junction or reparse point, any subfolder, or the data folder.
    /// </summary>
    public static void Run(IEnvStore env, bool clearDefault, string installDir, UninstallOptions? options = null)
    {
        options ??= new UninstallOptions();
        var dir = System.IO.Path.GetFullPath(installDir).TrimEnd('\\', '/');
        var dataDir = System.IO.Path.GetFullPath(options.DataDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clauderoo")).TrimEnd('\\', '/');
        AssertSafeToDelete(dir, dataDir);

        if ((options.IsTrayRunning ?? TrayIsRunning)())
            throw new ClauderooException("The clauderoo tray app is running. Quit it first (tray icon > Quit), then run uninstall again. Nothing was changed.");

        (options.SetAutoStart ?? (on => AutoStart.Set(on, "")))(false);

        var userPath = options.Path ?? new RegistryUserPath();
        var updated = Installer.RemoveFromPath(userPath.Read(), dir);
        if (updated != null) userPath.Write(updated);

        if (clearDefault) env.SetUser(ConfigDirVar, null);

        if (!Directory.Exists(dir)) return;
        MoveExesAside(dir);
        (options.Schedule ?? StartScheduled)(BuildRemoveArguments(dir));
    }

    private static bool TrayIsRunning()
    {
        var procs = Process.GetProcessesByName("clauderoow");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private static void AssertSafeToDelete(string dir, string dataDir)
    {
        // cmd expands %VAR% even inside quotes, so such a path could make the delayed rd hit something else.
        if (dir.Contains('%'))
            throw new ClauderooException($"Refusing to remove {dir}: the path contains '%', which cmd.exe would expand. Nothing was changed.");
        if (System.IO.Path.GetPathRoot(dir)?.TrimEnd('\\', '/').Equals(dir, StringComparison.OrdinalIgnoreCase) ?? true)
            throw new ClauderooException($"Refusing to remove {dir}: it is a drive root.");
        if (Same(dir, dataDir) || Inside(dataDir, dir) || Inside(dir, dataDir))
            throw new ClauderooException(
                $"Refusing to remove {dir}: it overlaps clauderoo's data folder {dataDir}, which is never touched.");
        if (!Directory.Exists(dir)) return;

        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ClauderooException(
                    $"Refusing to remove {dir}: {entry.Name} is a junction or link. Nothing was changed.");
            if (entry is DirectoryInfo)
                throw new ClauderooException(
                    $"Refusing to remove {dir}: it contains a folder ({entry.Name}); only the program files belong there. Nothing was changed.");
        }
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ClauderooException($"Refusing to remove {dir}: it is a junction or link. Nothing was changed.");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string child, string parent) =>
        child.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // A running exe can't be deleted but can be renamed; the delayed rd then removes what it can.
    private static void MoveExesAside(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.exe").ToList())
        {
            try { File.Move(f, f + ".old", overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Move(f, $"{f}.{Guid.NewGuid():N}.old"); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void StartScheduled(string arguments)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetTempPath(),
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
    }
}
