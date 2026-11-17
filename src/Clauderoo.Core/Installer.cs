using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Clauderoo.Core;

public static class Installer
{
    private static readonly string[] Files = ["clauderoo.exe", "clauderoow.exe"];
    public const string ShortAlias = "cr.exe";

    /// <summary>Copies the executables to %LOCALAPPDATA%\clauderoo and adds that folder to the User PATH.</summary>
    public static string Install(string sourceDir)
    {
        if (File.Exists(Path.Combine(sourceDir, "clauderoo.dll")))
            throw new ClauderooException(
                "Run install from the published single-file build (dotnet publish ... -p:PublishSingleFile=true).");

        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clauderoo");
        Directory.CreateDirectory(dest);
        var sameDir = string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\'), dest.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        DeleteStaleOld(dest);
        foreach (var f in Files)
        {
            var src = Path.Combine(sourceDir, f);
            if (!File.Exists(src)) throw new ClauderooException($"{f} not found in {sourceDir}.");
            if (sameDir) continue;
            Replace(src, Path.Combine(dest, f));
        }

        // Short alias: a copy of the CLI named cr.exe (works in cmd, PowerShell and Git Bash).
        Replace(Path.Combine(dest, "clauderoo.exe"), Path.Combine(dest, ShortAlias));

        // Read/write the raw registry value so REG_EXPAND_SZ entries (%VAR%) are preserved.
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
            ?? throw new ClauderooException("Could not open the user environment registry key.");
        var raw = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var updated = AppendToPath(raw, dest);
        if (updated != null)
        {
            key.SetValue("Path", updated, RegistryValueKind.ExpandString);
            BroadcastEnvironmentChange();
        }
        return dest;
    }

    /// <summary>
    /// Copies <paramref name="src"/> over <paramref name="dest"/>. A running exe (e.g. the in-tab `clauderoo run`
    /// wrapper of a session started from the tray) can't be overwritten but can be renamed, so an existing file is
    /// first moved aside to "*.old" (deleted by a later install once nothing runs it).
    /// </summary>
    internal static void Replace(string src, string dest)
    {
        string? aside = null;
        var movedAside = false;
        try
        {
            if (File.Exists(dest))
            {
                // A previous "*.old" can itself still be running: then use a unique name.
                aside = dest + ".old";
                try { File.Move(dest, aside, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    aside = $"{dest}.{Guid.NewGuid():N}.old";
                    File.Move(dest, aside);
                }
                movedAside = true;
            }
            File.Copy(src, dest, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The copy failed after the old file was moved aside: put it back so the install folder keeps working.
            if (movedAside) RestoreAside(aside!, dest);
            throw new ClauderooException(
                $"Couldn't replace {Path.GetFileName(dest)} — close clauderoo and any Claude sessions started from it, then try again.");
        }
    }

    private static void RestoreAside(string aside, string dest)
    {
        try { if (File.Exists(dest)) File.Delete(dest); } // a partial copy
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { File.Move(aside, dest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Best effort: removes "*.old" files left by earlier installs (ones still running stay).</summary>
    internal static void DeleteStaleOld(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.old"))
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    /// <summary>Tells running programs (Explorer, new terminals) that the environment changed.</summary>
    private static void BroadcastEnvironmentChange()
    {
        const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x2;
        try { SendMessageTimeout(new IntPtr(0xffff), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 5000, out _); }
        catch (Exception) { /* best effort */ }
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
