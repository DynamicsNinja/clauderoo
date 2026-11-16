using Clauderoo.Cli;
using Clauderoo.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8; // so ✓ ⚠ ✗ render

var paths = ClauderooPaths.ForCurrentUser();
var pm = new ProfileManager(paths, new WindowsEnvStore(), () => DateTime.Now);

return Commands.Run(args, Console.Out, Console.Error, pm,
    startNewWindow: spec =>
    {
        using var p = Launcher.Start(spec); // new terminal window: don't wait
        return 0;
    },
    runInPlace: spec =>
    {
        using var p = Launcher.Start(spec);
        // Ctrl+C belongs to claude while it runs; don't let it kill the wrapper first.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        p.WaitForExit();
        return p.ExitCode;
    });
