using System;
using System.Runtime.InteropServices;

namespace LinuxDevices
{
    /// <summary>
    /// Helper class to send signals to other processes
    /// </summary>
    public static class Signal
    {
        /// <summary>
        /// Signal to ask a process for graceful termination
        /// </summary>
        public const int SIGTERM = 15;

        /// <summary>
        /// Send a signal to another process
        /// </summary>
        /// <param name="pid">Target process ID</param>
        /// <param name="signal">Signal number</param>
        /// <exception cref="ArgumentException"></exception>
        public static void Kill(int pid, int signal)
        {
            if (Interop.kill(pid, signal) < 0)
            {
                throw new ArgumentException($"Failed to send signal (error {Marshal.GetLastWin32Error()})");
            }
        }
    }
}
