using System.Runtime.InteropServices;

namespace Clauderoo.Cli;

/// <summary>Puts text on the Windows clipboard from a console app (no WinForms), on its own STA thread.</summary>
internal static class NativeClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>True when the text was copied. Never throws.</summary>
    public static bool SetText(string text)
    {
        var ok = false;
        var t = new Thread(() =>
        {
            try { ok = TrySet(text); }
            catch (Exception) { ok = false; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return t.Join(TimeSpan.FromSeconds(5)) && ok;
    }

    private static bool TrySet(string text)
    {
        var opened = false;
        for (var i = 0; i < 10 && !opened; i++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened) Thread.Sleep(50); // another app may hold it briefly
        }
        if (!opened) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = (text.Length + 1) * 2;
            var mem = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) return false;
            var ptr = GlobalLock(mem);
            if (ptr == IntPtr.Zero) { GlobalFree(mem); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { GlobalUnlock(mem); }
            if (SetClipboardData(CfUnicodeText, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
            return true; // the clipboard owns mem now
        }
        finally { CloseClipboard(); }
    }
}
