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
