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
            if (!sameDir) File.Copy(src, Path.Combine(dest, f), overwrite: true);
        }

        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(p => string.Equals(p.Trim().TrimEnd('\\'), dest, StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("PATH", string.Join(';', parts.Append(dest)), EnvironmentVariableTarget.User);
        return dest;
    }
}
