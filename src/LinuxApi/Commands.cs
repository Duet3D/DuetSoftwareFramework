using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LinuxApi
{
    /// <summary>
    /// Helper function to create a symlink on UNIX-based operating systems
    /// </summary>
    public static class Commands
    {
        /// <summary>
        /// Change the permissions of a file or directory
        /// </summary>
        /// <param name="file">File to modify</param>
        /// <param name="user">User permissions</param>
        /// <param name="group">Group permissions</param>
        /// <param name="any">Other permissions</param>
        /// <exception cref="IOException">Operation failed</exception>
        public static void Chmod(string file, UnixPermissions user, UnixPermissions group, UnixPermissions any)
        {
            int mode = 0;
            mode |= ((int)user) << 6;
            mode |= ((int)group) << 3;
            mode |= (int)any;
            if (Interop.chmod(file, (ushort)mode) < 0)
            {
                throw new IOException($"Failed to change permissions of {file} (error {Marshal.GetLastWin32Error()})");
            }
        }

        /// <summary>
        /// Change the owner or a file or directory
        /// </summary>
        /// <param name="file">File to change</param>
        /// <param name="uid">New user ID</param>
        /// <param name="gid">New group ID</param>
        /// <param name="resolveSymlinks">True if symbolic links may be resolved</param>
        /// <exception cref="IOException">Operation failed</exception>
        public static void Chown(string file, int uid, int gid, bool resolveSymlinks = true)
        {
            int result = resolveSymlinks ? Interop.chown(file, uid, gid) : Interop.lchown(file, uid, gid);
            if (result < 0)
            {
                throw new IOException($"Failed to change owner of {file} (error {Marshal.GetLastWin32Error()})");
            }
        }

        /// <summary>
        /// Change the owner of a file descriptor
        /// </summary>
        /// <param name="fd">File descriptor</param>
        /// <param name="uid">User ID</param>
        /// <param name="gid">Group ID</param>
        public static void Chown(int fd, int uid, int gid)
        {
            if (Interop.fchown(fd, uid, gid) < 0)
            {
                throw new IOException($"Failed to change owner of FD {fd} (error {Marshal.GetLastWin32Error()})");
            }
        }

        /// <summary>
        /// Send a signal to another process
        /// </summary>
        /// <param name="pid">Target process ID</param>
        /// <param name="signal">Signal number</param>
        /// <exception cref="ArgumentException">Failed to send signal to process</exception>
        public static void Kill(int pid, Signal signal)
        {
            if (Interop.kill(pid, (int)signal) < 0)
            {
                throw new ArgumentException($"Failed to send signal (error {Marshal.GetLastWin32Error()})");
            }
        }

        /// <summary>
        /// Get the owner user and group IDs
        /// </summary>
        /// <param name="pathname">Name of the file</param>
        /// <param name="uid">User ID</param>
        /// <param name="gid">Group ID</param>
        public static void Stat(string pathname, out int uid, out int gid)
        {
            statbuf buffer = new statbuf();
            if (Interop.stat(Interop.STATVER, pathname, ref buffer) < 0)
            {
                throw new ArgumentException($"Failed to get file info (error {Marshal.GetLastWin32Error()})");
            }
            uid = buffer.st_uid;
            gid = buffer.st_gid;
        }

        /// <summary>
        /// Create a new symlink
        /// </summary>
        /// <param name="name1">Source file</param>
        /// <param name="name2">Target file</param>
        /// <exception cref="IOException">Failed to create symbolic link</exception>
        public static void Symlink(string name1, string name2)
        {
            if (Interop.symlink(name1, name2) < 0)
            {
                throw new IOException($"Failed to create symlink (error {Marshal.GetLastWin32Error()})");
            }
        }
    }
}
