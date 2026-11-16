using Clauderoo.Cli;
using Clauderoo.Core;

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
        p.WaitForExit();
        return p.ExitCode;
    });
