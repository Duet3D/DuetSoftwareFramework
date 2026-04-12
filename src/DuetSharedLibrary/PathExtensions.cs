using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Extension members for <see cref="Path"/>
/// </summary>
public static class PathExtensions
{
    extension(Path)
    {
        /// <summary>
        /// Resolve a path to its canonical form, following all symlinks and resolving
        /// ".." components through the actual filesystem (not lexically).
        /// This is equivalent to the POSIX realpath() function.
        /// </summary>
        /// <param name="path">Path to resolve</param>
        /// <returns>Canonical path, or null if the path does not exist</returns>
        public static string? GetRealPath(string path)
        {
            IntPtr result = Interop.realpath(path, IntPtr.Zero);
            if (result == IntPtr.Zero)
            {
                return null;
            }
            string resolved = Marshal.PtrToStringUTF8(result)!;
            Marshal.FreeHGlobal(result);
            return resolved;
        }
    }
}
