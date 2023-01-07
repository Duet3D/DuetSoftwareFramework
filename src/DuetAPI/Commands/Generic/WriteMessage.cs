using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Write an arbitrary generic message
    /// </summary>
    /// <remarks>If neither <c>OutputMessage</c> nor <c>LogMessage</c> is true, the message is written to the console output</remarks>
    [RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.ObjectModelReadWrite)]
    public class WriteMessage : Command
    {
        /// <summary>
        /// Type of the message to write
        /// </summary>
        public MessageType Type { get; set; }

        /// <summary>
        /// Content of the message to write
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Output the message on the console and via the object model
        /// </summary>
        public bool OutputMessage { get; set; } = true;

        /// <summary>
        /// Write the message to the log file (if applicable)
        /// </summary>
        [Obsolete("Deprecated in favor of LogLevel")]
        public bool LogMessage { get; set; }

        /// <summary>
        /// Log level of this message
        /// </summary>
        public LogLevel? LogLevel { get; set; }
    }
}
