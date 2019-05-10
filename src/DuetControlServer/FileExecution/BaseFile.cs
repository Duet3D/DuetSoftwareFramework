using DuetAPI;
using DuetControlServer.Commands;
using System;
using System.IO;
using System.Threading.Tasks;
using Zhaobang.IO;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Base class for files that read G-codes line by line
    /// </summary>
    public class BaseFile : IDisposable
    {
        private FileStream _fileStream;
        private SeekableStreamReader _reader;

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
        public long Position {
            get => _fileStream.Position;
            set => _fileStream.Seek(value, SeekOrigin.Begin);
        }

        /// <summary>
        /// Returns the length of the file in bytes
        /// </summary>
        public long Length { get => _fileStream.Length; }

        /// <summary>
        /// Indicates if this file is supposed to be aborted
        /// </summary>
        public bool IsAborted { get; private set; }

        /// <summary>
        /// Request cancellation of this file
        /// </summary>
        public void Abort() => IsAborted = true;

        /// <summary>
        /// Indicates if the file has been finished
        /// </summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Create a file reader
        /// </summary>
        /// <param name="fileName">Name of the file to process</param>
        /// <param name="channel">Channel to send the codes to</param>
        public BaseFile(string fileName, CodeChannel channel)
        {
            _fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            _reader = new SeekableStreamReader(_fileStream);

            FileName = fileName;
            Channel = channel;
        }

        /// <summary>
        /// Read the next available code
        /// </summary>
        /// <returns>Read code or null if none found</returns>
        public virtual async Task<Code> ReadCode()
        {
            // Deal with abort requests
            if (IsAborted)
            {
                if (!IsFinished)
                {
                    _fileStream.Close();
                    IsFinished = true;
                }
                return null;
            }

            // Attempt to read the next line
            long filePosition = _reader.Position;
            string line = await _reader.ReadLineAsync();
            if (line != null)
            {
                return new Code(line)
                {
                    FilePosition = filePosition,
                    Channel = Channel
                };
            }

            // End of file
            IsFinished = true;
            _fileStream.Close();
            return null;
        }

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            _fileStream.Dispose();
        }
    }
}
