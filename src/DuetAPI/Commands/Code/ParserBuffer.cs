using System;
using System.IO;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Internal buffer for reading asynchronously from files
    /// </summary>
    public sealed class CodeParserBuffer
    {
        /// <summary>
        /// Default constructor of this class
        /// </summary>
        /// <param name="bufferSize">How many bytes to buffer when reading from a file</param>
        /// <param name="isFile">Indicates if line numbers and file positions are valid</param>
        public CodeParserBuffer(int bufferSize, bool isFile)
        {
            Content = new byte[bufferSize];
            IsFile = isFile;
            LineNumber = isFile ? (long?)1 : null;
        }

        /// <summary>
        /// Indicates if a NL was seen before
        /// </summary>
        internal bool SeenNewLine = true;

        /// <summary>
        /// Last indentation level
        /// </summary>
        internal byte Indent;

        /// <summary>
        /// Whether the line started with G53
        /// </summary>
        internal bool EnforcingAbsolutePosition;

        /// <summary>
        /// Buffer content
        /// </summary>
        internal readonly byte[] Content;

        /// <summary>
        /// Pointer in the buffer
        /// </summary>
        internal int Pointer;

        /// <summary>
        /// How many bytes are available for reading
        /// </summary>
        internal int Size;

        /// <summary>
        /// Invalidate the buffer internally
        /// </summary>
        internal void InvalidateData()
        {
            SeenNewLine = true;
            Indent = 0;
            EnforcingAbsolutePosition = false;
        }

        /// <summary>
        /// Indicates if this buffer is used for reading from a file
        /// </summary>
        internal bool IsFile;

        /// <summary>
        /// Current line number
        /// </summary>
        public long? LineNumber;

        /// <summary>
        /// Last major G-code to repeat
        /// </summary>
        public int LastGCode = -1;

        /// <summary>
        /// Indicates if the last code may be repeated as per Fanuc or LaserWeb style
        /// </summary>
        public bool MayRepeatCode;

        /// <summary>
        /// Invalidate the buffer
        /// </summary>
        public void Invalidate()
        {
            InvalidateData();
            Pointer = Size = 0;
            LineNumber = null;
            LastGCode = -1;
        }

        /// <summary>
        /// Get the actual byte position when reading from a stream
        /// </summary>
        /// <param name="reader">Stream reader to read from</param>
        /// <returns>Actual position in bytes</returns>
        [Obsolete("This call is deprecated because the buffer position of a StreamReader is not accessible. Pass your stream directly instead")]
        public long GetPosition(StreamReader reader) => reader.BaseStream.Position - Size + Pointer;

        /// <summary>
        /// Get the actual byte position when reading from a stream
        /// </summary>
        /// <param name="stream">Stream to read from</param>
        /// <returns>Actual position in bytes</returns>
        public long GetPosition(Stream stream) => stream.Position - Size + Pointer;
    }
}
