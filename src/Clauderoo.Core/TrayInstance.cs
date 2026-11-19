namespace Clauderoo.Core;

/// <summary>
/// The running tray app, seen from outside: whether it runs, and asking it to restart itself (after an upgrade, so
/// the new version takes over). The tray answers a restart request on <see cref="RestartAckEvent"/>; a tray from
/// before this existed doesn't, and the caller falls back to telling the user.
/// </summary>
public static class TrayInstance
{
    public const string MutexName = @"Local\clauderoo-tray";
    public const string RestartEvent = @"Local\clauderoo-tray-restart";
    public const string RestartAckEvent = @"Local\clauderoo-tray-restart-ack";
    /// <summary>The tray's argument for "an older tray is closing: wait for it instead of exiting".</summary>
    public const string RestartedArgument = "--restarted";

    public static bool IsRunning() => Mutex.TryOpenExisting(MutexName, out var m) && Dispose(m);

    /// <summary>
    /// Asks the running tray to restart from <c>clauderoow.exe</c> in the install folder. True when it acknowledged
    /// within <paramref name="ackWait"/>; false when no tray runs or it is too old to understand the request.
    /// </summary>
    public static bool RequestRestart(TimeSpan ackWait)
    {
        if (!IsRunning()) return false;
        using var ack = new EventWaitHandle(false, EventResetMode.AutoReset, RestartAckEvent);
        ack.Reset();
        using var request = new EventWaitHandle(false, EventResetMode.AutoReset, RestartEvent);
        request.Set();
        if (ack.WaitOne(ackWait)) return true;
        // Nobody took it: clear it, or the next tray to start would restart itself straight away.
        request.Reset();
        return false;
    }

    private static bool Dispose(Mutex m) { m.Dispose(); return true; }
}
