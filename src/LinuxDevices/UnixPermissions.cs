using System;

namespace LinuxApi
{
    /// <summary>
    /// Enumeration of supported UNIX permissions
    /// </summary>
    [Flags]
    public enum UnixPermissions : ushort
    {
        /// <summary>
        /// Read permission
        /// </summary>
        Read = 4,

        /// <summary>
        /// Write permission
        /// </summary>
        Write = 2,

        /// <summary>
        /// Execute permission
        /// </summary>
        Execute = 1,

        /// <summary>
        /// No permissions
        /// </summary>
        None = 0
    }
}
