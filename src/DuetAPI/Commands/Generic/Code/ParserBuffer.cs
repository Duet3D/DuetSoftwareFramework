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
        /// <param name="lineNumbersValid">Indicates if line numbers are valid</param>
        public CodeParserBuffer(int bufferSize, bool lineNumbersValid)
        {
            Buffer = new char[bufferSize];
            LineNumber = lineNumbersValid ? (long?)1 : null;
        }

        /// <summary>
        /// Indicates if the last 
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
        /// Internal buffer
        /// </summary>
        internal readonly char[] Buffer;

        /// <summary>
        /// Pointer in the buffer
        /// </summary>
        internal int BufferPointer;

        /// <summary>
        /// How many bytes are available for reading
        /// </summary>
        internal int BufferSize;

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
        /// Current line number
        /// </summary>
        public long? LineNumber;

        /// <summary>
        /// Invalidate the buffer
        /// </summary>
        public void Invalidate()
        {
            InvalidateData();
            BufferSize = 0;
            LineNumber = null;
        }

        /// <summary>
        /// Get the actual byte position when reading from a stream
        /// </summary>
        /// <param name="reader">Reader to read from</param>
        /// <returns>Actual position in bytes</returns>
        public long GetPosition(StreamReader reader) => reader.BaseStream.Position - BufferSize + BufferPointer;
    }
}
