using DuetAPI;
using DuetControlServer.Commands;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Base class for files that read G-codes line by line
    /// </summary>
    public class BaseFile : IDisposable
    {
        private FileStream _fileStream;
        private StreamReader _reader;
        private bool _isAbortRequested;

        /// <summary>
        /// File path to the file being executed
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Channel to send the codes to
        /// </summary>
        public CodeChannel Channel { get; }

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
            _reader = new StreamReader(_fileStream);

            FileName = fileName;
            Channel = channel;
        }

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            _fileStream.Dispose();
        }

        /// <summary>
        /// Read the next available code
        /// </summary>
        /// <returns>Read code or null if none found</returns>
        public virtual async Task<Code> ReadCode()
        {
            // Deal with abort requests
            if (_isAbortRequested)
            {
                if (!IsFinished)
                {
                    IsFinished = true;
                    _fileStream.Close();
                }
                return null;
            }

            // Attempt to read the next line
            long filePosition = _fileStream.Position;
            string line = await _reader.ReadLineAsync();
            if (line != null)
            {
                return new Code(line)
                {
                    FilePosition = (uint?)filePosition,
                    Channel = Channel
                };
            }

            // End of file
            IsFinished = true;
            _fileStream.Close();
            return null;
        }

        /// <summary>
        /// Go to the specified position
        /// </summary>
        /// <param name="position"></param>
        public void Seek(long position)
        {
            _fileStream.Seek(position, SeekOrigin.Begin);
        }

        /// <summary>
        /// Request cancellation of this file
        /// </summary>
        public void Abort()
        {
            _isAbortRequested = true;
        }
    }
}
