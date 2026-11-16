using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Clauderoo.Core;

public static class Installer
{
    private static readonly string[] Files = ["clauderoo.exe", "clauderoow.exe"];

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
        if (updated != null)
        {
            key.SetValue("Path", updated, RegistryValueKind.ExpandString);
            BroadcastEnvironmentChange();
        }
        return dest;
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
