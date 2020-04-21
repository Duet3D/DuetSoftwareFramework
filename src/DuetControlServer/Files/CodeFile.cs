using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Class to read G/M/T-codes from files
    /// </summary>
    public sealed class CodeFile : IDisposable
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Dictionary holding the currently open code files
        /// </summary>
        private static readonly List<CodeFile>[] _openFiles = new List<CodeFile>[Inputs.Total];

        /// <summary>
        /// Lock for accessing the list of open files
        /// </summary>
        private static readonly AsyncLock _openFilesLock = new AsyncLock();

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static CodeFile()
        {
            for (int i = 0; i < Inputs.Total; i++)
            {
                _openFiles[i] = new List<CodeFile>();
            }
        }

        /// <summary>
        /// Abort all running macro files on a given code channel
        /// </summary>
        /// <param name="channel">Code channel</param>
        /// <returns>Asynchronous task</returns>
        private static async Task AbortAll(CodeChannel channel)
        {
            int numChannel = (int)channel;
            using (await _openFilesLock.LockAsync(Program.CancellationToken))
            {
                foreach (CodeFile file in _openFiles[numChannel])
                {
                    using (await file.LockAsync())
                    {
                        file.Close();
                    }
                }
            }
        }

        /// <summary>
        /// Internal lock
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Lock this instance
        /// </summary>
        /// <returns>Disposable lock</returns>
        public IDisposable Lock() => _lock.Lock(Program.CancellationToken);

        /// <summary>
        /// Lock this instance asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync(Program.CancellationToken);

        /// <summary>
        /// File being read from
        /// </summary>
        private readonly FileStream _fileStream;

        /// <summary>
        /// Reader for the file stream
        /// </summary>
        private readonly StreamReader _reader;

        /// <summary>
        /// Internal buffer used for reading from files
        /// </summary>
        private readonly CodeParserBuffer _parserBuffer = new CodeParserBuffer(Settings.FileBufferSize, true);

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
        private readonly Stack<CodeBlock> _codeBlocks = new Stack<CodeBlock>();

        /// <summary>
        /// Last code block
        /// </summary>
        private CodeBlock _lastCodeBlock;

        /// <summary>
        /// Internal queue of codes read used to determine when pending codes have been processed
        /// </summary>
        private readonly Queue<Code> _pendingCodes = new Queue<Code>();

        /// <summary>
        /// Gets or sets the current file position in bytes
        /// </summary>
        public long Position
        {
            get => _position;
            set
            {
                if (!IsClosed)
                {
                    _fileStream.Seek(value, SeekOrigin.Begin);
                    _reader.DiscardBufferedData();
                    _position = value;
                    _parserBuffer.Invalidate();
                    _parserBuffer.LineNumber = LineNumber = (value == 0) ? (long?)1 : null;
                }
            }
        }
        private long _position;

        /// <summary>
        /// Get the current number of iterations of the current loop
        /// </summary>
        /// <param name="code">Code that requested the number of iterations</param>
        /// <returns>Number of iterations</returns>
        /// <exception cref="CodeParserException">Query came outside a while loop</exception>
        public int GetIterations(Code code)
        {
            foreach (CodeBlock codeBlock in _codeBlocks)
            {
                if (codeBlock.StartingCode.Keyword == KeywordType.While)
                {
                    return codeBlock.Iterations;
                }
            }
            throw new CodeParserException("'iterations' used when not inside a loop", code);
        }

        /// <summary>
        /// Result of the last G/M/T-code (0 = success, 1 = warning, 2 = error)
        /// </summary>
        public int LastResult { get; set; }

        /// <summary>
        /// Number of the current line
        /// </summary>
        public long? LineNumber { get; private set; } = 1;

        /// <summary>
        /// Returns the length of the file in bytes
        /// </summary>
        public long Length { get => _fileStream.Length; }

        /// <summary>
        /// Indicates if this file is closed
        /// </summary>
        public bool IsClosed { get; private set; }

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

            lock (_openFiles)
            {
                _openFiles[(int)channel].Add(this);
            }
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
        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            lock (_openFiles)
            {
                _openFiles[(int)Channel].Remove(this);
            }

            if (disposing)
            {
                using (_lock.Lock())
                {
                    IsClosed = true;
                    _reader.Dispose();
                    _fileStream.Dispose();
                }
            }

            disposed = true;
        }

        /// <summary>
        /// Read the next available code and interpret conditional codes except for echo
        /// </summary>
        /// <returns>Read code or null if none found</returns>
        /// <exception cref="CodeParserException">Failed to read the next code</exception>
        /// <exception cref="OperationCanceledException">Failed to flush the pending codes</exception>
        /// <remarks>
        /// This instance must NOT be locked when this is called
        /// </remarks>
        public async Task<Code> ReadCodeAsync()
        {
            while (true)
            {
                // Read the next available code
                bool codeRead;
                Code code = new Code
                {
                    Channel = Channel,
                    File = this,
                    LineNumber = LineNumber,
                    FilePosition = Position
                };

                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    if (IsClosed)
                    {
                        return null;
                    }

                    while (_pendingCodes.TryPeek(out Code pendingCode) && pendingCode.IsExecuted)
                    {
                        // Clean up the pending codes whenever a new code is read to save memory
                        _pendingCodes.Dequeue();
                    }

                    do
                    {
                        codeRead = await DuetAPI.Commands.Code.ParseAsync(_reader, code, _parserBuffer);
                        _position += code.Length.Value;
                        LineNumber = code.LineNumber;
                    }
                    while (!codeRead && _parserBuffer.GetPosition(_reader) < _fileStream.Length);

                    if (codeRead)
                    {
                        _logger.Trace("Read code {0}", code);
                    }
                }

                // Check if this is the end of the last block(s)
                bool readAgain = false;
                while (_codeBlocks.TryPeek(out CodeBlock state))
                {
                    if (!codeRead || ((code.Keyword != KeywordType.None || code.Type != CodeType.Comment) && code.Indent <= state.StartingCode.Indent))
                    {
                        if (state.StartingCode.Keyword == KeywordType.If ||
                            state.StartingCode.Keyword == KeywordType.ElseIf ||
                            state.StartingCode.Keyword == KeywordType.Else)
                        {
                            // End of conditional block
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                _logger.Debug("End of {0} block", state.StartingCode.Keyword);
                                _lastCodeBlock = _codeBlocks.Pop();
                            }
                        }
                        else if (state.StartingCode.Keyword == KeywordType.While)
                        {
                            if (state.ProcessBlock && !state.SeenCodes)
                            {
                                throw new CodeParserException("empty while loop detected", code);
                            }

                            if (!codeRead || code.FilePosition != state.StartingCode.FilePosition)
                            {
                                // End of while loop
                                if (state.ProcessBlock || state.ContinueLoop)
                                {
                                    await WaitForPendingCodes();
                                    using (await _lock.LockAsync(Program.CancellationToken))
                                    {
                                        Position = state.StartingCode.FilePosition.Value;
                                        _parserBuffer.LineNumber = LineNumber = state.StartingCode.LineNumber;
                                        state.ContinueLoop = false;
                                        state.Iterations++;
                                        readAgain = true;
                                        _logger.Debug("Restarting {0} block, iterations = {1}", state.StartingCode.Keyword, state.Iterations);
                                    }
                                    break;
                                }

                                using (await _lock.LockAsync(Program.CancellationToken))
                                {
                                    _logger.Debug("End of {0} block", state.StartingCode.Keyword);
                                    _lastCodeBlock = _codeBlocks.Pop();
                                }
                            }
                            else
                            {
                                // Restarting while loop
                                break;
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                if (readAgain)
                {
                    // Parse the while loop including condition once again
                    continue;
                }

                // Check if any more codes could be read
                if (!codeRead)
                {
                    using (await _lock.LockAsync(Program.CancellationToken))
                    {
                        Close();
                    }
                    return null;
                }

                // Process only codes where the corresponding condition is met
                if (!_codeBlocks.TryPeek(out CodeBlock codeBlock) || codeBlock.ProcessBlock)
                {
                    // FIXME If/ElseIf/Else/While are not sent to the interceptors
                    if (codeBlock != null && (code.Keyword != KeywordType.While || code.FilePosition != codeBlock.StartingCode.FilePosition))
                    {
                        codeBlock.SeenCodes = true;
                    }

                    switch (code.Keyword)
                    {
                        case KeywordType.If:
                        case KeywordType.ElseIf:
                        case KeywordType.While:
                            // Check elif condition
                            if (code.Keyword == KeywordType.ElseIf)
                            {
                                if (_lastCodeBlock == null || _lastCodeBlock.StartingCode.Indent != code.Indent ||
                                    (_lastCodeBlock.StartingCode.Keyword != KeywordType.If && _lastCodeBlock.StartingCode.Keyword != KeywordType.ElseIf))
                                {
                                    throw new CodeParserException("unexpected elif condition", code);
                                }

                                if (!_lastCodeBlock.ExpectingElse)
                                {
                                    // Last if/elif condition was true, ignore the following block
                                    _logger.Debug("Skipping {0} block", code.Keyword);
                                    using (await _lock.LockAsync(Program.CancellationToken))
                                    {
                                        _codeBlocks.Push(new CodeBlock
                                        {
                                            StartingCode = code
                                        });
                                    }
                                    break;
                                }
                            }

                            // Start a new conditional block if necessary
                            await WaitForPendingCodes();
                            _logger.Debug("Evaluating {0} block", code.Keyword);
                            if (code.Keyword != KeywordType.While || codeBlock == null || codeBlock.StartingCode.FilePosition != code.FilePosition)
                            {
                                using (await _lock.LockAsync(Program.CancellationToken))
                                {
                                    codeBlock = new CodeBlock
                                    {
                                        StartingCode = code
                                    };
                                    _codeBlocks.Push(codeBlock);
                                }
                            }

                            // Evaluate the condition
                            string evaluationResult = await Model.Expressions.Evaluate(code, false);
                            if (evaluationResult != "true" && evaluationResult != "false")
                            {
                                throw new CodeParserException($"invalid conditional result '{evaluationResult}', must be either true or false", code);
                            }
                            _logger.Debug("Evaluation result: ", evaluationResult);
                            codeBlock.ProcessBlock = (evaluationResult == "true");
                            codeBlock.ExpectingElse = (code.Keyword != KeywordType.While && evaluationResult == "false");
                            break;

                        case KeywordType.Else:
                            if (_lastCodeBlock == null || _lastCodeBlock.StartingCode.Indent != code.Indent ||
                                (_lastCodeBlock.StartingCode.Keyword != KeywordType.If && _lastCodeBlock.StartingCode.Keyword != KeywordType.ElseIf))
                            {
                                throw new CodeParserException("unexpected else", code);
                            }

                            // else condition is true if the last if/elif condition was false
                            _logger.Debug("{0} {1} block", _lastCodeBlock.ExpectingElse ? "Starting" : "Skipping", code.Keyword);
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                _codeBlocks.Push(new CodeBlock
                                {
                                    StartingCode = code,
                                    ProcessBlock = _lastCodeBlock.ExpectingElse
                                });
                            }
                            break;

                        case KeywordType.Break:
                        case KeywordType.Continue:
                            if (!_codeBlocks.Any(codeBlock => codeBlock.StartingCode.Keyword == KeywordType.While))
                            {
                                throw new CodeParserException("break or continue cannot be called outside while loop", code);
                            }
                            _logger.Debug("Doing {0}", code.Keyword);

                            foreach (CodeBlock state in _codeBlocks)
                            {
                                state.ProcessBlock = false;
                                if (state.StartingCode.Keyword == KeywordType.While)
                                {
                                    state.ContinueLoop = (code.Keyword == KeywordType.Continue);
                                    break;
                                }
                            }
                            break;

                        case KeywordType.Abort:
                            _logger.Debug("Doing {0}", code.Keyword);
                            await AbortAll(Channel);
                            return code;

                        case KeywordType.Return:
                            _logger.Debug("Doing {0}", code.Keyword);
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                Close();
                            }
                            return code;

                        case KeywordType.Echo:
                        case KeywordType.None:
                            if (codeBlock != null)
                            {
                                codeBlock.SeenCodes = true;
                            }
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                _pendingCodes.Enqueue(code);
                            }
                            return code;

                        default:
                            throw new CodeParserException($"Keyword {code.Keyword} is not supported", code);
                    }
                }
            }
        }

        /// <summary>
        /// Wait for pending codes from the file being read
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        private async Task WaitForPendingCodes()
        {
            if (_pendingCodes.TryPeek(out _))
            {
                while (await SPI.Interface.Flush(Channel))
                {
                    using (await _lock.LockAsync(Program.CancellationToken))
                    {
                        if (IsClosed)
                        {
                            // Canot continue if the file has been closed
                            throw new OperationCanceledException();
                        }

                        while (_pendingCodes.TryPeek(out Code pendingCode) && pendingCode.IsExecuted)
                        {
                            // Remove executed codes from the internal queue
                            _pendingCodes.Dequeue();
                        }

                        if (!_pendingCodes.TryPeek(out _))
                        {
                            // No more pending codes, resume normal code execution
                            return;
                        }
                    }
                }
                throw new OperationCanceledException();
            }
        }

        /// <summary>
        /// Close this file
        /// </summary>
        public void Close()
        {
            if (IsClosed)
            {
                return;
            }
            IsClosed = true;
            _reader.Close();
            _fileStream.Close();
        }
    }
}
