using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clauderoo.Cli;

/// <summary>
/// One Windows job object per process, with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: processes added to it (and the children
/// they start afterwards) are killed when this process exits and the OS closes the job handle. Used for delegated
/// claude runs, so they never outlive the MCP server. Best effort: a failure leaves the child unjobbed.
/// </summary>
internal static class KillOnCloseJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    private static readonly Lazy<IntPtr> Job = new(Create);

    public static bool Add(Process p)
    {
        try
        {
            var job = Job.Value;
            return job != IntPtr.Zero && AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // The handle is deliberately never closed: closing it is what kills the children, at process exit.
    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(job, ExtendedLimitInformation, ptr, (uint)size)) return job;
        }
        finally { Marshal.FreeHGlobal(ptr); }
        CloseHandle(job);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount,
            OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
