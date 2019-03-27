using System;

namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Flags describing the message
    /// </summary>
    [Flags]
    public enum CodeReplyFlags : byte
    {
        /// <summary>
        /// This is a warning message
        /// </summary>
        Warning = 1,
        
        /// <summary>
        /// This is an error message
        /// </summary>
        Error = 2,
        
        /// <summary>
        /// This is a final code response
        /// </summary>
        CodeComplete = 128
    }
}