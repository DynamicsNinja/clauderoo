namespace Clauderoo.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var manage = args.Any(a => string.Equals(a, "--manage", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, @"Local\clauderoo-tray", out var createdNew);
        if (!createdNew)
        {
            // The tray is already running: ask it to open the Profiles window.
            if (manage) ManageSignal.Send();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(openProfiles: manage);
        Application.Run(context);
    }
}
