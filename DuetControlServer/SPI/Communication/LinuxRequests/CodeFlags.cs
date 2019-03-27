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
        /// Indicates that this code can be used for pause/resume
        /// </summary>
        Pausable = 1,
        
        /// <summary>
        /// Indicates that G53 was used with this code
        /// </summary>
        EnforceAbsolutePosition = 2,
        
        /// <summary>
        /// Indicates that this code originates from config.g
        /// </summary>
        FromConfig = 4,
        
        /// <summary>
        /// Indicates that this code originates from config-override.g
        /// </summary>
        FromConfigOverride = 8,
    }
}