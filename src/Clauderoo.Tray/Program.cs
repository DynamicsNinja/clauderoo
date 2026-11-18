namespace Clauderoo.Tray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // The scheduled auto-switch task (cr auto --schedule on): run `clauderoo auto --once` without a console window.
        if (args.Length == 1 && string.Equals(args[0], Clauderoo.Core.AutoSchedule.TrayArgument, StringComparison.OrdinalIgnoreCase))
            return HiddenAutoOnce.Run();

        var manage = args.Any(a => string.Equals(a, "--manage", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, @"Local\clauderoo-tray", out var createdNew);
        if (!createdNew)
        {
            // The tray is already running: ask it to open the Profiles window.
            if (manage) ManageSignal.Send();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(openProfiles: manage);
        Application.Run(context);
        return 0;
    }
}

/// <summary>Runs clauderoo.exe (next to this exe) as `auto --once` with no window and returns its exit code.</summary>
internal static class HiddenAutoOnce
{
    public static int Run()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "clauderoo.exe");
        if (!File.Exists(cli)) return 1;
        var psi = new System.Diagnostics.ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("auto");
        psi.ArgumentList.Add("--once");
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return 1;
            // Drained so the child never blocks on a full pipe; the result is its exit code (Task Scheduler's Last Run Result).
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return 1;
            }
            Task.WaitAll(o, e);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 1; }
    }
}
