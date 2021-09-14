using System;

namespace DuetHttpClient.Utility
{
    /// <summary>
    /// Item class for file lists
    /// </summary>
    public sealed record FileListItem
    {
        /// <summary>
        /// Indicates if this item is a directory or a file
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// Name of the file or directory
        /// </summary>
        public string Filename { get; set; }

        /// <summary>
        /// Size of the file
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Date and time of the last modification 
        /// </summary>
        public DateTime LastModified { get; set; }
    }
}
