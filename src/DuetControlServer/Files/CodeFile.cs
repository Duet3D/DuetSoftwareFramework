using DuetAPI;
using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        /// Internal lock
        /// </summary>
        private readonly AsyncLock _lock = new();

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
        private readonly CodeParserBuffer _parserBuffer = new(Settings.FileBufferSize, true);

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
        private readonly Stack<CodeBlock> _codeBlocks = new();

        /// <summary>
        /// Last code block
        /// </summary>
        private CodeBlock _lastCodeBlock;

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
                    _parserBuffer.LineNumber = LineNumber = (value == 0) ? 1 : null;
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
        public long Length => _fileStream.Length;

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

            _fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
            _reader = new StreamReader(_fileStream, Encoding.UTF8, false, Settings.FileBufferSize, true);
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
        private bool _disposed;

        /// <summary>
        /// Dispose this instance internally
        /// </summary>
        /// <param name="disposing">True if this instance is being disposed</param>
        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                IsClosed = true;
                _reader.Dispose();
                _fileStream.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Read the next available code and interpret conditional codes performing flow control
        /// </summary>
        /// <param name="sharedCode">Code that may be reused</param>
        /// <returns>Read code or null if none found</returns>
        /// <exception cref="CodeParserException">Failed to read the next code</exception>
        /// <exception cref="OperationCanceledException">Failed to flush the pending codes</exception>
        /// <remarks>
        /// This instance must NOT be locked when this is called
        /// </remarks>
        public async Task<Code> ReadCodeAsync(Code sharedCode = null)
        {
            while (true)
            {
                // Prepare the result
                Code code = sharedCode ?? new Code();
                code.Channel = Channel;
                code.File = this;
                code.LineNumber = LineNumber;
                code.FilePosition = Position;

                // Read the next available code
                bool codeRead;
                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    if (IsClosed)
                    {
                        return null;
                    }

                    do
                    {
                        // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            _parserBuffer.MayRepeatCode = Model.Provider.Get.State.MachineMode == DuetAPI.ObjectModel.MachineMode.CNC ||
                                                          Model.Provider.Get.State.MachineMode == DuetAPI.ObjectModel.MachineMode.Laser;
                        }

                        // Get the next code
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
                    if (!codeRead || (code.Type != CodeType.Comment && state.IsFinished(code.Indent)))
                    {
                        if (state.HasLocalVariables)
                        {
                            // Wait for pending commands to be executed so all the local variables can be disposed of again
                            await Codes.Processor.FlushAsync(Channel);
                        }

                        if (state.StartingCode.Keyword == KeywordType.While)
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
                                    if (!state.HasLocalVariables)
                                    {
                                        // Wait for pending codes to be fully executed so that "iterations" can be incremented
                                        await Codes.Processor.FlushAsync(Channel);
                                    }

                                    using (await _lock.LockAsync(Program.CancellationToken))
                                    {
                                        Position = state.StartingCode.FilePosition.Value;
                                        _parserBuffer.LineNumber = LineNumber = state.StartingCode.LineNumber;
                                        state.ProcessBlock = true;
                                        state.ContinueLoop = false;
                                        state.Iterations++;
                                        await DeleteLocalVariables(state);
                                        state.HasLocalVariables = false;
                                        readAgain = true;
                                        _logger.Debug("Restarting {0} block, iterations = {1}", state.StartingCode.Keyword, state.Iterations);
                                    }
                                    break;
                                }
                                await EndCodeBlock();
                            }
                            else
                            {
                                // Restarting while loop
                                break;
                            }
                        }
                        else
                        {
                            // End of generic code block
                            await EndCodeBlock();
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
                    // RRF cleans up the local variables automatically when a file is closed, no need to do it here as well
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
                                        _codeBlocks.Push(new CodeBlock(code, false));
                                    }
                                    break;
                                }
                            }

                            // Start a new conditional block if necessary
                            await Codes.Processor.FlushAsync(Channel);
                            _logger.Debug("Evaluating {0} block", code.Keyword);
                            if (code.Keyword != KeywordType.While || codeBlock == null || codeBlock.StartingCode.FilePosition != code.FilePosition)
                            {
                                using (await _lock.LockAsync(Program.CancellationToken))
                                {
                                    codeBlock = new CodeBlock(code, false);
                                    _codeBlocks.Push(codeBlock);
                                }
                            }

                            // Evaluate the condition
                            string stringEvaluationResult = await Model.Expressions.Evaluate(code, true);
                            if (bool.TryParse(stringEvaluationResult, out bool evaluationResult))
                            {
                                _logger.Debug("Evaluation result: ({0}) = {1}", code.KeywordArgument, evaluationResult);
                                codeBlock.ProcessBlock = evaluationResult;
                                codeBlock.ExpectingElse = (code.Keyword != KeywordType.While) && !evaluationResult;
                            }
                            else
                            {
                                throw new CodeParserException($"invalid conditional result '{stringEvaluationResult}', must be either true or false", code);
                            }
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
                                _codeBlocks.Push(new CodeBlock(code, _lastCodeBlock.ExpectingElse));
                            }
                            break;

                        case KeywordType.Break:
                        case KeywordType.Continue:
                            if (!_codeBlocks.Any(codeBlock => codeBlock.StartingCode.Keyword == KeywordType.While))
                            {
                                throw new CodeParserException("break or continue cannot be called outside while loop", code);
                            }

                            _logger.Debug("Doing {0}", code.Keyword);
                            await Codes.Processor.FlushAsync(Channel);

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
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                Close();
                            }
                            return code;

                        case KeywordType.Var:
                            if (codeBlock == null || code.Indent > codeBlock.StartingCode.Indent)
                            {
                                using (await _lock.LockAsync(Program.CancellationToken))
                                {
                                    codeBlock = new CodeBlock(code, true)
                                    {
                                        HasLocalVariables = true
                                    };
                                    _codeBlocks.Push(codeBlock);
                                }
                            }
                            else
                            {
                                codeBlock.HasLocalVariables = true;
                            }
                            return code;

                        case KeywordType.Echo:
                        case KeywordType.Global:
                        case KeywordType.None:
                        case KeywordType.Set:
                            return code;

                        default:
                            throw new CodeParserException($"Keyword {code.Keyword} is not supported", code);
                    }
                }

                // Make a new shared code so it isn't reused in code blocks
                if (sharedCode != null)
                {
                    sharedCode = new Code();
                }
            }
        }

        /// <summary>
        /// Add a new local variable to the current code block
        /// </summary>
        /// <param name="varName">Name of the variable</param>
        public void AddLocalVariable(string varName)
        {
            if (_codeBlocks.TryPeek(out CodeBlock codeBlock))
            {
                if (!codeBlock.LocalVariables.Contains(varName))
                {
                    codeBlock.LocalVariables.Add(varName);
                }
            }
            else
            {
                _logger.Warn("Cannot add local variable because there is no open code block");
            }
        }

        /// <summary>
        /// Delete local variables from a given code block
        /// </summary>
        /// <param name="codeBlock">Code block</param>
        /// <returns>Asynchronous task</returns>
        public async Task DeleteLocalVariables(CodeBlock codeBlock)
        {
            Task[] deletionTasks = new Task[codeBlock.LocalVariables.Count];
            for (int i = 0; i < codeBlock.LocalVariables.Count; i++)
            {
                deletionTasks[i] = SPI.Interface.SetVariable(Channel, false, codeBlock.LocalVariables[i], null);
            }
            await Task.WhenAll(deletionTasks);
            codeBlock.LocalVariables.Clear();
        }

        /// <summary>
        /// Called to finish the current code block
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task EndCodeBlock()
        {
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                if (_codeBlocks.TryPop(out CodeBlock codeBlock))
                {
                    // Log the end of this block
                    if (codeBlock.StartingCode.Keyword == KeywordType.If ||
                        codeBlock.StartingCode.Keyword == KeywordType.ElseIf ||
                        codeBlock.StartingCode.Keyword == KeywordType.Else ||
                        codeBlock.StartingCode.Keyword == KeywordType.While)
                    {
                        _logger.Debug("End of {0} block", codeBlock.StartingCode.Keyword);
                    }
                    else
                    {
                        _logger.Debug("End of generic block");
                    }

                    // Delete previously created local variables
                    await DeleteLocalVariables(codeBlock);

                    // End
                    _lastCodeBlock = codeBlock;
                }
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
        }
    }
}
