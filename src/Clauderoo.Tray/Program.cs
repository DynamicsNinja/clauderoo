namespace Clauderoo.Tray;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\clauderoo-tray", out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
    }
}
