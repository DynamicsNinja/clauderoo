using System.Diagnostics;

namespace Clauderoo.Core;

public interface IGcloud
{
    string? Find();
    (int exit, string stdout, string stderr) Run(params string[] args);
}

public sealed class Gcloud : IGcloud
{
    private const string Cmd = "gcloud.cmd";
    private const int TimeoutMs = 60_000;

    public string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), Cmd);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Google", "Cloud SDK", "google-cloud-sdk", "bin", Cmd);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public (int exit, string stdout, string stderr) Run(params string[] args)
    {
        var exe = Find() ?? throw new ClauderooException("gcloud not found — install it with: winget install Google.CloudSDK");
        // .cmd files must run through cmd.exe; ArgumentList keeps each argument separate.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new ClauderooException("Couldn't start gcloud.");
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return (-1, "", "gcloud timed out");
        }
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>Opens a new console running the Google sign-in; the UI calls this.</summary>
    public void OpenSignIn()
    {
        var exe = Find() ?? throw new ClauderooException("gcloud not found — install it with: winget install Google.CloudSDK");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("application-default");
        psi.ArgumentList.Add("login");
        Process.Start(psi)?.Dispose();
    }
}
