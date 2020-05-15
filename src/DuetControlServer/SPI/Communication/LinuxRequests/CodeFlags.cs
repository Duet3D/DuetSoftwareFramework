using System;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Flags of a G/M/T-code
    /// </summary>
    [Flags]
    public enum CodeFlags : byte
    {
        /// <summary>
        /// This code has a valid major code
        /// </summary>
        HasMajorCommandNumber = 1,

        /// <summary>
        /// This code has a valid minor code
        /// </summary>
        HasMinorCommandNumber = 2,

        /// <summary>
        /// This code has a valid file position (for pausing)
        /// </summary>
        HasFilePosition = 4,

        /// <summary>
        /// Indicates that G53 was used with this code (absolute positioning)
        /// </summary>
        EnforceAbsolutePosition = 8
    }
}