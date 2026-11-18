using Clauderoo.Cli;
using Clauderoo.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8; // so ✓ ⚠ ✗ render

var paths = ClauderooPaths.ForCurrentUser();
var pm = new ProfileManager(paths, new WindowsEnvStore(), () => DateTime.Now) { WtFragmentFile = WtFragment.DefaultFile() };

var version = typeof(Commands).Assembly.GetName().Version?.ToString(3) ?? "0";
using var usageHandler = UsageClient.CreateHandler();
using var usage = new UsageClient(usageHandler, () => DateTimeOffset.UtcNow, version);

return Commands.Run(args, Console.Out, Console.Error, pm,
    startNewWindow: spec =>
    {
        using var p = Launcher.Start(spec, pm.Sessions); // new terminal window: don't wait
        return 0;
    },
    runInPlace: spec =>
    {
        using var p = Launcher.Start(spec, pm.Sessions);
        // Ctrl+C belongs to claude while it runs; don't let it kill the wrapper first.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        p.WaitForExit();
        return p.ExitCode;
    },
    usage: usage);
