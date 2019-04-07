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
        /// No major command number set
        /// </summary>
        NoMajorCommandNumber = 1,

        /// <summary>
        /// No minor command number set
        /// </summary>
        NoMinorCommandNumber = 2,

        /// <summary>
        /// Indicates if the file position is valid
        /// </summary>
        FilePositionValid = 4,
        
        /// <summary>
        /// Indicates that G53 was used with this code
        /// </summary>
        EnforceAbsolutePosition = 8
    }
}