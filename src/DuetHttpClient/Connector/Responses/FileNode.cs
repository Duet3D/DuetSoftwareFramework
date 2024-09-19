using System;

namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Class representing a file item in SBC mode
    /// </summary>
    internal class FileNode
    {
        /// <summary>
        /// Date and time of the last modification
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// Name of the file or directory
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Size of the file in bytes
        /// </summary>
        public long Size { get; set; }
        
        /// <summary>
        /// Type of the file (f = file, d = directory)
        /// </summary>
        public char Type { get; set; }
    }
}