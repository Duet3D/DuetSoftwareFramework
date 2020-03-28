using DuetAPI;
using DuetControlServer.Commands;
using System;
using System.IO;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Base class for files that read G-codes line by line
    /// </summary>
    public class BaseFile : IDisposable
    {
        /// <summary>
        /// File being read from
        /// </summary>
        private readonly FileStream _fileStream;

        /// <summary>
        /// Reader for the file stream
        /// </summary>
        private readonly StreamReader _reader;

        /// <summary>
        /// File path to the file being executed
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Channel to send the codes to
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Gets or sets the current file position in bytes
        /// </summary>
        public long Position
        {
            get => _position;
            set
            {
                if (!IsAborted)
                {
                    IsFinished = false;
                    _fileStream.Seek(value, SeekOrigin.Begin);
                    _reader.DiscardBufferedData();
                    _position = value;
                    LineNumber = (value == 0) ? (long?)0 : null;
                }
            }
        }
        private long _position;

        /// <summary>
        /// Number of the current line
        /// </summary>
        public long? LineNumber { get; private set; } = 0;

        /// <summary>
        /// Returns the length of the file in bytes
        /// </summary>
        public long Length { get => _fileStream.Length; }

        /// <summary>
        /// Indicates if this file is supposed to be aborted
        /// </summary>
        public bool IsAborted { get; private set; }

        /// <summary>
        /// Indicates if the file has been finished
        /// </summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Request cancellation of this file
        /// </summary>
        public virtual void Abort() => IsAborted = IsFinished = true;

        /// <summary>
        /// Constructor of the base class for reading from a G-code file
        /// </summary>
        /// <param name="fileName">Name of the file to process or null if it is optional</param>
        /// <param name="channel">Channel to send the codes to</param>
        public BaseFile(string fileName, CodeChannel channel)
        {
            FileName = fileName;
            Channel = channel;

            _fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            _reader = new StreamReader(_fileStream);
        }

        /// <summary>
        /// Finalizer of a base file
        /// </summary>
        ~BaseFile() => Dispose(false);

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed;

        /// <summary>
        /// Dispose this instance internally
        /// </summary>
        /// <param name="disposing">True if this instance is being disposed</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                _reader.Dispose();
                _fileStream.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Read the next available code
        /// </summary>
        /// <returns>Read code or null if none found</returns>
        public virtual Code ReadCode()
        {
            // Deal with closed files
            if (IsFinished || IsAborted)
            {
                return null;
            }

            // Read the next available non-empty code and keep track of the line number
            Code code = new Code()
            {
                Channel = Channel,
                LineNumber = LineNumber,
                FilePosition = Position
            };

            bool codeRead, enforcingAbsolutePosition = false;
            do
            {
                codeRead = DuetAPI.Commands.Code.Parse(_reader, code, ref enforcingAbsolutePosition);
                _position += code.Length.Value;
                LineNumber = code.LineNumber;
            }
            while (!codeRead && !_reader.EndOfStream);

            // Return it
            if (!codeRead)
            {
                IsFinished = true;
                return null;
            }
            return code;
        }
    }
}
