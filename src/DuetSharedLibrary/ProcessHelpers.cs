using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

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
}
