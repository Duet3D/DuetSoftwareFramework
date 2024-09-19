using System;

namespace DuetHttpClient.Connector.Responses
{
    /// <summary>
    /// Internal class for filelist items
    /// </summary>
    internal class FileItem
    {
        /// <summary>
        /// Type of the file (f = file, d = directory)
        /// </summary>
        public char Type { get; set; }

        /// <summary>
        /// Name of the file or directory
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Size of the file in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Date and time of the last modification
        /// </summary>
        public DateTime Date { get; set; }
    }

    /// <summary>
    /// File list response in standalone mode
    /// </summary>
    internal class FileListResponse : ErrResponse
    {
        //public string dir { get; set; }
        public int First { get; set; }
        public FileItem[] Files { get; set; } = [];
        public int Next { get; set; }
    }
}