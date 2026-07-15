using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    // Scheduling policies (see sched.h)
    internal const int SCHED_OTHER = 0;
    internal const int SCHED_FIFO = 1;
    internal const int SCHED_RR = 2;

    /// <summary>
    /// Scheduling parameter block. For SCHED_FIFO/SCHED_RR only the priority is meaningful
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct sched_param
    {
        internal int sched_priority;
    }

    // pid=0 targets the calling thread; cpusetsize is the size of the mask in bytes
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    // pid=0 targets the calling thread
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int sched_setscheduler(int pid, int policy, ref sched_param param);

    // Minimum/maximum valid priority for a given policy
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int sched_get_priority_min(int policy);

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int sched_get_priority_max(int policy);
}
