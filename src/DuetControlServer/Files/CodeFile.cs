using DuetAPI;
using DuetAPI.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Class to read G/M/T-codes from files
    /// </summary>
    public class CodeFile : IDisposable
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
        /// Internal stack for conditional G-code execution
        /// </summary>
        private readonly Stack<CodeFileState> _stack = new Stack<CodeFileState>();

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
        /// Whether this is the first code to parse or a NL was parsed
        /// </summary>
        private bool _seenNewLine = true;

        /// <summary>
        /// Whether G53 is in effect
        /// </summary>
        private bool _enforcingAbsolutePosition;

        /// <summary>
        /// Current indentation level
        /// </summary>
        private byte _indent;

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
        /// Internal cancellation token source used for codes
        /// </summary>
        private CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

        /// <summary>
        /// Cancellation token that is triggered when the file is cancelled/aborted
        /// </summary>
        public CancellationToken CancellationToken { get => _cts.Token; }

        /// <summary>
        /// Cancel pending codes
        /// </summary>
        public void CancelPendingCodes()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
        }

        /// <summary>
        /// Request cancellation of this file
        /// </summary>
        public virtual void Abort()
        {
            if (IsAborted)
            {
                return;
            }
            IsAborted = IsFinished = true;
            _cts.Cancel();
        }

        /// <summary>
        /// Constructor of the base class for reading from a G-code file
        /// </summary>
        /// <param name="fileName">Name of the file to process or null if it is optional</param>
        /// <param name="channel">Channel to send the codes to</param>
        public CodeFile(string fileName, CodeChannel channel)
        {
            FileName = fileName;
            Channel = channel;

            _fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            _reader = new StreamReader(_fileStream);
        }

        /// <summary>
        /// Finalizer of a base file
        /// </summary>
        ~CodeFile() => Dispose(false);

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
                _cts.Dispose();
            }

            disposed = true;
        }

        /// <summary>
        /// Read the next available code and interpret conditional codes except for echo
        /// </summary>
        /// <returns>Read code or null if none found</returns>
        /// <exception cref="CodeParserException">Failed to read the next code</exception>
        public Task<Code> ReadCodeAsync()
        {
            // Deal with closed files
            if (IsFinished || IsAborted)
            {
                return Task.FromResult<Code>(null);
            }

            do
            {
                // Read the next available non-empty code
                Code code = new Code
                {
                    CancellationToken = _cts.Token,
                    Channel = Channel,
                    Flags = _enforcingAbsolutePosition ? CodeFlags.EnforceAbsolutePosition : CodeFlags.None,
                    Indent = _indent,
                    LineNumber = LineNumber,
                    FilePosition = Position
                };

                bool codeRead;
                do
                {
                    codeRead = DuetAPI.Commands.Code.Parse(_reader, code, ref _seenNewLine);
                    _position += code.Length.Value;
                    LineNumber = code.LineNumber;

                    // Check if a NL has been parsed
                    _indent = _seenNewLine ? (byte)0 : code.Indent;
                    _enforcingAbsolutePosition = _seenNewLine ? false : code.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition);
                }
                while (!codeRead && !_reader.EndOfStream);

                // Check if this is the end of the last block
                CodeFileState lastBlock;
                if (_stack.TryPeek(out lastBlock) && (!codeRead || code.Indent <= lastBlock.StartingCode.Indent))
                {
                    lastBlock.Iterations++;
                }

                // Check if any more codes could be read
                if (!codeRead)
                {
                    IsFinished = true;
                    return Task.FromResult<Code>(null);
                }

#if true
                return Task.FromResult(code);
#else
                // Check for conditional G-code
                switch (code.Keyword)
                {
                    case KeywordType.If:
                        if (await SPI.Interface.Flush(Channel))
                        {
                            string expression = ""; // await Model.ExpressionParser.PrepareExpression(code.KeywordArgument);
                            _stack.Push(new CodeFileState
                            {
                                StartingCode = code,
                                LastResult = await SPI.Interface.EvaluateExpression(Channel, expression)
                            });
                        }
                        break;

                    case KeywordType.Echo:
                    case KeywordType.None:
                        return code;
                }
#endif
            }
            while (true);
        }
    }
}
