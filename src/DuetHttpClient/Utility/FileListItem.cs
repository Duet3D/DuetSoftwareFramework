using System;

namespace DuetHttpClient.Utility
{
    /// <summary>
    /// Item class for file lists
    /// </summary>
    public sealed class FileListItem
    {
        /// <summary>
        /// Indicates if this item is a directory or a file
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// Name of the file or directory
        /// </summary>
        public string Filename { get; set; } = string.Empty;

        /// <summary>
        /// Size of the file
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Date and time of the last modification 
        /// </summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// Get the hash code of this instance
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode() => IsDirectory.GetHashCode() ^ Filename.GetHashCode() ^ Size.GetHashCode() ^ LastModified.GetHashCode();

        /// <summary>
        /// Check if this instance equals another
        /// </summary>
        /// <param name="obj">Other instance</param>
        /// <returns>Whether both instances are equal</returns>
        public override bool Equals(object? obj) => obj is FileListItem other && IsDirectory == other.IsDirectory && Filename == other.Filename && Size == other.Size && LastModified == other.LastModified;

        /// <summary>
        /// Convert this instance to a string
        /// </summary>
        /// <returns></returns>
        public override string ToString() => Filename;
    }
}
