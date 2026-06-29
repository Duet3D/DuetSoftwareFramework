using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    // pid=0 targets the calling thread; cpusetsize is the size of the mask in bytes
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);
}
