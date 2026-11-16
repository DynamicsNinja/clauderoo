using Microsoft.Win32;

namespace Clauderoo.Core;

public static class Installer
{
    private static readonly string[] Files = ["clauderoo.exe", "clauderoow.exe"];

    /// <summary>Copies the executables to %LOCALAPPDATA%\clauderoo and adds that folder to the User PATH.</summary>
    public static string Install(string sourceDir)
    {
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clauderoo");
        Directory.CreateDirectory(dest);
        var sameDir = string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\'), dest.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        foreach (var f in Files)
        {
            var src = Path.Combine(sourceDir, f);
            if (!File.Exists(src)) throw new ClauderooException($"{f} not found in {sourceDir}.");
            if (sameDir) continue;
            try { File.Copy(src, Path.Combine(dest, f), overwrite: true); }
            catch (IOException)
            {
                throw new ClauderooException($"Couldn't replace {f} — quit the clauderoo tray and try again.");
            }
        }

        // Read/write the raw registry value so REG_EXPAND_SZ entries (%VAR%) are preserved.
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? throw new ClauderooException("Could not open the user environment registry key.");
        var raw = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var updated = AppendToPath(raw, dest);
        if (updated != null) key.SetValue("Path", updated, RegistryValueKind.ExpandString);
        // No WM_SETTINGCHANGE broadcast: the caller tells the user to open a new terminal.
        return dest;
    }

    /// <summary>Returns rawPath with dir appended, or null when dir is already an entry (raw or expanded).</summary>
    public static string? AppendToPath(string rawPath, string dir)
    {
        var want = Norm(dir);
        foreach (var entry in rawPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = entry.Trim().Trim('"');
            if (Norm(e) == want || Norm(Environment.ExpandEnvironmentVariables(e)) == want) return null;
        }
        if (rawPath.Length == 0) return dir;
        return rawPath.EndsWith(';') ? rawPath + dir : rawPath + ";" + dir;
    }

    private static string Norm(string p) => p.TrimEnd('\\', '/').ToUpperInvariant();
}
