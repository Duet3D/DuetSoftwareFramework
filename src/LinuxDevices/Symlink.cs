using System.IO;
using System.Runtime.InteropServices;

namespace LinuxDevices
{
    /// <summary>
    /// Helper function to create a symlink on UNIX-based operating systems
    /// </summary>
    public static class Symlink
    {
        /// <summary>
        /// Create a new symlink
        /// </summary>
        /// <param name="name1">Source file</param>
        /// <param name="name2">Target file</param>
        public static void Create(string name1, string name2)
        {
            if (Interop.symlink(name1, name2) < 0)
            {
                throw new IOException($"Failed to create symlink (error {Marshal.GetLastWin32Error()})");
            }
        }
    }
}
