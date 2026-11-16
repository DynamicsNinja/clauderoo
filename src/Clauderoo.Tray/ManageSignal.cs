using System.Runtime.InteropServices;

namespace Clauderoo.Tray;

// `clauderoow.exe --manage` while the tray is running: the new process sets a named event and exits;
// the tray waits on it on a background thread and opens (or brings to front) the Profiles window.
static class ManageSignal
{
    private const string EventName = @"Local\clauderoo-manage";
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Called by the second process: lets the tray take the foreground, then signals it.</summary>
    public static void Send()
    {
        AllowSetForegroundWindow(AsfwAny);
        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        ev.Set();
    }

    /// <summary>Called by the tray: runs <paramref name="onSignal"/> (on a background thread) for each signal.</summary>
    public static EventWaitHandle Listen(Action onSignal)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (ev.WaitOne()) onSignal();
            }
            catch (ObjectDisposedException) { /* tray shutting down */ }
        })
        { IsBackground = true, Name = "clauderoo-manage-listener" };
        thread.Start();
        return ev;
    }
}
