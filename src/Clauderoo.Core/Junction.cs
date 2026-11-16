using System.Diagnostics;

namespace Clauderoo.Core;

public static class Junction
{
    public static void Create(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new ClauderooException("Could not start cmd.exe");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new ClauderooException($"Could not create junction {link} -> {target}: {(stderr.Result + stdout.Result).Trim()}");
    }

    public static string? GetTarget(string path)
    {
        // LinkTarget is readable even when the junction's target is gone (dangling).
        return new DirectoryInfo(path).LinkTarget;
    }
}
