using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Helper class to retrieve process information and perform operations on processes
/// </summary>
public static class ProcessHelpers
{
    /// <summary>
    /// Get the effective group ID
    /// </summary>
    /// <returns>Effective group ID</returns>
    public static int GetEffectiveGroupID() => Interop.getegid();

    /// <summary>
    /// Get the effective user ID
    /// </summary>
    /// <returns>Effective user ID</returns>
    public static int GetEffectiveUserID() => Interop.geteuid();

    /// <summary>
    /// Ask the process to terminate by sending SIGTERM to it
    /// </summary>
    /// <param name="process">Process</param>
    /// <exception cref="IOException">Failed to kill process</exception>
    public static void Terminate(this Process process)
    {
        int error = Interop.kill(process.Id, Interop.SIGTERM);
        if (error < 0)
        {
            throw new IOException($"Failed to kill process (error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>
    /// Read the parent PID of a given process from /proc/{pid}/stat
    /// </summary>
    /// <param name="pid">Process ID to look up</param>
    /// <returns>Parent PID, or 0 if it could not be determined</returns>
    public static int GetParentPid(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // Field 2 (comm) is parenthesised and may contain spaces, so scan past the final ')' to reach the PPID
            int lastParen = stat.LastIndexOf(')');
            if (lastParen < 0 || lastParen + 2 >= stat.Length)
            {
                return 0;
            }
            string[] fields = stat[(lastParen + 2)..].Split(' ');
            return (fields.Length >= 2) && int.TryParse(fields[1], out int ppid) ? ppid : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Pin the calling thread to a specific CPU core using sched_setaffinity.
    /// </summary>
    /// <param name="coreId">Zero-based CPU core index to pin to</param>
    /// <returns>True if the affinity was set successfully</returns>
    public static bool PinCurrentThreadToCore(int coreId)
    {
        ulong mask = 1UL << coreId;
        return Interop.sched_setaffinity(0, (IntPtr)sizeof(ulong), ref mask) == 0;
    }

    /// <summary>
    /// Switch the calling thread to the SCHED_FIFO real-time scheduling policy at the given priority.
    /// This is what actually gives a thread deterministic, preemptive-over-CFS latency on a PREEMPT_RT
    /// kernel; plain thread affinity or <see cref="System.Threading.ThreadPriority"/> (which only maps to
    /// a nice value on Linux) does not. Requires CAP_SYS_NICE or a suitable RLIMIT_RTPRIO
    /// </summary>
    /// <param name="priority">Real-time priority (1..99); higher preempts lower</param>
    /// <returns>True if the scheduling policy was applied successfully</returns>
    public static bool SetCurrentThreadRealtimePriority(int priority)
    {
        int min = Interop.sched_get_priority_min(Interop.SCHED_FIFO);
        int max = Interop.sched_get_priority_max(Interop.SCHED_FIFO);
        if (min >= 0 && max >= min)
        {
            // Clamp to the range the kernel actually accepts for SCHED_FIFO
            priority = Math.Clamp(priority, min, max);
        }

        Interop.sched_param param = new() { sched_priority = priority };
        return Interop.sched_setscheduler(0, Interop.SCHED_FIFO, ref param) == 0;
    }

    public static bool IsRaspberryPi()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase) && line.Contains("BCM"))
                    return true;
                if (line.StartsWith("Model", StringComparison.OrdinalIgnoreCase) && line.Contains("Raspberry Pi"))
                    return true;
            }
        }
        catch (IOException)
        {
            // Not on Linux or /proc/cpuinfo unavailable
        }
        return false;
    }

    /// <summary>
    /// Check whether the process was exec'd with <c>AT_SECURE=1</c>. Parses /proc/{pid}/auxv looking for the AT_SECURE
    /// entry (type 23) set to non-zero. The kernel sets this when the exec crosses a privilege boundary (setuid,
    /// setgid, or file capabilities), causing glibc to ignore LD_PRELOAD and related environment variables. The bit is
    /// immutable for the life of the process so it cannot be stripped after the fact
    /// </summary>
    /// <param name="process">Process</param>
    /// <returns>True if the process's exec was secure-mode</returns>
    public static bool IsExecSecure(this Process process)
    {
        try
        {
            ReadOnlySpan<byte> auxv = File.ReadAllBytes($"/proc/{process.Id}/auxv");
            int wordSize = IntPtr.Size;
            for (int offset = 0; offset + 2 * wordSize <= auxv.Length; offset += 2 * wordSize)
            {
                ulong type = ReadWord(auxv.Slice(offset, wordSize));
                if (type == 0)
                {
                    // AT_NULL terminator
                    return false;
                }
                if (type == 23)
                {
                    // AT_SECURE
                    return ReadWord(auxv.Slice(offset + wordSize, wordSize)) != 0;
                }
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static ulong ReadWord(ReadOnlySpan<byte> bytes) => bytes.Length == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
