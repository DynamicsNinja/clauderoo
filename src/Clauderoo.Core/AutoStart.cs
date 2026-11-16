using Microsoft.Win32;

namespace Clauderoo.Core;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "clauderoo";

    public static void Set(bool on, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, $"\"{exePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }
}
