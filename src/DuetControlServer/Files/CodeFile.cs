using DuetAPI;
using DuetAPI.Commands;
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
    /// <param name="fileName">Name of the file to process or null if it is optional</param>
    /// <param name="physicalFile">Physical path to the file</param>
    /// <param name="channel">Channel to send the codes to</param>
    public class CodeFile(string fileName, string physicalFile, CodeChannel channel) : IDisposable
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
        private readonly FileStream _fileStream = new(physicalFile, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);

        /// <summary>
        /// Internal buffer used for reading from files
        /// </summary>
        private readonly CodeParserBuffer _parserBuffer = new(Settings.FileBufferSize, true);

        /// <summary>
        /// Current line number in the file
        /// </summary>
        public long LineNumber => _parserBuffer.LineNumber ?? 0;

        /// <summary>
        /// File path to the file being executed
        /// </summary>
        public string FileName { get; } = fileName;

        /// <summary>
        /// Physical file path to the file being executed
        /// </summary>
        public string PhysicalFileName { get; } = physicalFile;

        /// <summary>
        /// Channel to send the codes to
        /// </summary>
        public CodeChannel Channel { get; } = channel;

        /// <summary>
        /// Internal stack for conditional G-code execution
        /// </summary>
        private readonly Stack<CodeBlock> _codeBlocks = new();

        /// <summary>
        /// Blocks whose scope the read-ahead has left. The firmware may still be executing codes from inside them
        /// and a pause can rewind into their scope, so their local variables are kept in the firmware under hidden
        /// names until the file is closed or the block is closed again. Guarded by the lock on <see cref="_codeBlocks"/>
        /// </summary>
        private readonly List<CodeBlock> _closedBlocks = [];

        /// <summary>
        /// Whether a pause can rewind this file into a block that the read-ahead has already left
        /// </summary>
        protected virtual bool IsRewindable => true;

        /// <summary>
        /// Last ended code block
        /// </summary>
        private CodeBlock? _lastCodeBlock;

        /// <summary>
        /// Gets or sets the current file position in bytes
        /// </summary>
        public long Position
        {
            get => _parserBuffer.GetPosition(_fileStream);
            set
            {
                if (!IsClosed)
                {
                    _fileStream.Seek(value, SeekOrigin.Begin);
                    _parserBuffer.Invalidate();
                    _parserBuffer.LineNumber = (value == 0) ? 1 : null;
                    NextFilePosition = value;
                }
            }
        }

        /// <summary>
        /// File position of the next code to actually run
        /// </summary>
        public long NextFilePosition { get; set; }

        /// <summary>
        /// Seek to a new file position and restore the code block state the read-ahead had at that position.
        /// Blocks entered at or past it are dropped because their lines are read again after the seek, so blocks
        /// left on the stack would be pushed twice and while loops would restart with bogus iteration counts;
        /// their local variables are deleted so that their re-declaration succeeds. The same goes for variables
        /// that surviving blocks declared at or past it. Already closed blocks whose scope contains the target
        /// position are pushed back onto the stack and their hidden local variables get their names back.
        /// The caller must hold the lock of this file
        /// </summary>
        /// <param name="position">New file position</param>
        /// <returns>Asynchronous task</returns>
        public async Task RewindAsync(long position)
        {
            Position = position;

            // The firmware processes variable requests in the order they are queued here
            List<Task> varTasks = [];
            lock (_codeBlocks)
            {
                while (_codeBlocks.TryPeek(out CodeBlock? codeBlock) && codeBlock.FilePosition >= position)
                {
                    _codeBlocks.Pop();
                    if (codeBlock.HasLocalVariables)
                    {
                        varTasks.Add(DeleteLocalVariables(codeBlock));
                    }
                    _logger.Debug("Dropped {0} block starting at byte {1} after seeking to byte {2}", codeBlock.Keyword, codeBlock.FilePosition, position);
                }

                foreach (CodeBlock codeBlock in _codeBlocks)
                {
                    codeBlock.LocalVariables.RemoveAll(variable =>
                    {
                        if (variable.Position >= position)
                        {
                            varTasks.Add(SPI.Interface.SetVariable(Channel, false, variable.Name, null));
                            return true;
                        }
                        return false;
                    });
                }

                _closedBlocks.RemoveAll(codeBlock =>
                {
                    if (codeBlock.FilePosition >= position)
                    {
                        DeleteHiddenVariables(codeBlock, varTasks);
                        _logger.Debug("Dropped closed {0} block starting at byte {1} after seeking to byte {2}", codeBlock.Keyword, codeBlock.FilePosition, position);
                        return true;
                    }
                    return false;
                });

                // Push closed blocks containing the target position back onto the stack, outermost first.
                // They can only be nested inside each other and inside the surviving stack blocks.
                // A block that a loop has entered again in the meantime is still on the stack and stays as it is
                foreach (CodeBlock codeBlock in _closedBlocks.Where(codeBlock => codeBlock.EndPosition >= position && !_codeBlocks.Any(openBlock => openBlock.FilePosition == codeBlock.FilePosition)).OrderBy(codeBlock => codeBlock.FilePosition).ToList())
                {
                    _closedBlocks.Remove(codeBlock);
                    RestoreLocalVariables(codeBlock, position, varTasks);
                    _codeBlocks.Push(codeBlock);
                    _logger.Debug("Restored {0} block starting at byte {1} after seeking to byte {2}", codeBlock.Keyword, codeBlock.FilePosition, position);
                }
            }
            await Task.WhenAll(varTasks);
        }

        /// <summary>
        /// Get the current number of iterations of the current loop
        /// </summary>
        /// <param name="code">Code that requested the number of iterations</param>
        /// <returns>Number of iterations</returns>
        /// <exception cref="CodeParserException">Query came outside a while loop</exception>
        public int GetIterations(Code code)
        {
            lock (_codeBlocks)
            {
                foreach (CodeBlock codeBlock in _codeBlocks)
                {
                    if (codeBlock.Keyword == KeywordType.While)
                    {
                        return codeBlock.Iterations;
                    }
                }
            }
            throw new CodeParserException("'iterations' used when not inside a loop", code);
        }

        /// <summary>
        /// Returns the length of the file in bytes
        /// </summary>
        public long Length => _fileStream.Length;

        /// <summary>
        /// Indicates if this file is closed
        /// </summary>
        public bool IsClosed => _isClosed;
        private volatile bool _isClosed = false;

        /// <summary>
        /// Close this file
        /// </summary>
        public void Close() => _isClosed = true;

        /// <summary>
        /// Copy constructor
        /// </summary>
        /// <param name="copyFrom">File to copy from</param>
        /// <param name="channel">Channel where the file is executed</param>
        public CodeFile(CodeFile copyFrom, CodeChannel channel) : this(copyFrom.FileName, copyFrom.PhysicalFileName, channel)
        {
            // Copy conditional states
            _codeBlocks.Clear();
            foreach (CodeBlock block in copyFrom._codeBlocks.Reverse())
            {
                _codeBlocks.Push(block with { });
            }
            foreach (CodeBlock block in copyFrom._closedBlocks)
            {
                _closedBlocks.Add(block with { });
            }
            _lastCodeBlock = (copyFrom._lastCodeBlock is not null) ? copyFrom._lastCodeBlock with { } : null;

            // Seek to the next code's file position and shallow-copy the parser state
            Position = copyFrom.NextFilePosition;

            _parserBuffer.LineNumber = copyFrom._parserBuffer.LineNumber;
            _parserBuffer.LastGCode = copyFrom._parserBuffer.LastGCode;
        }

        /// <summary>
        /// Finalizer of a base file
        /// </summary>
        ~CodeFile() => Dispose(false);

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public virtual void Dispose()
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
                _isClosed = true;
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
        public async Task<Code?> ReadCodeAsync(Code? sharedCode = null)
        {
            Code code = sharedCode ?? new Code();
            bool resetCode = sharedCode is not null;
            while (true)
            {
                // Start with a fresh code instance
                if (resetCode)
                {
                    code.Reset();
                }
                else
                {
                    resetCode = true;
                }
                code.Channel = Channel;
                code.File = this;

                // Read the next available code
                bool codeRead;
                using (await LockAsync())
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
                        codeRead = await DuetAPI.Commands.Code.ParseAsync(_fileStream, code, _parserBuffer);
                    }
                    while (!codeRead && _parserBuffer.GetPosition(_fileStream) < _fileStream.Length);

                    if (codeRead)
                    {
                        _logger.Trace("Read code {0}", code);
                    }
                }

                // Check if this is the end of the last block(s)
                bool readAgain = false;
                while (_codeBlocks.TryPeek(out CodeBlock? state))
                {
                    if (!codeRead || (code.Type != CodeType.Comment && state.IsFinished(code.Indent)))
                    {
                        if (state.HasLocalVariables)
                        {
                            // Wait for pending commands to be executed so all the local variables can be disposed of again.
                            // We don't care if this fails, because we need to tidy up in any case
                            await Codes.Processor.FlushAsync(this);
                        }

                        if (state.Keyword == KeywordType.While)
                        {
                            if (state.ProcessBlock && !state.SeenCodes)
                            {
                                throw new CodeParserException("empty while loop detected", code);
                            }

                            if (!codeRead || code.FilePosition != state.FilePosition)
                            {
                                // End of while loop
                                if (state.ProcessBlock || state.ContinueLoop)
                                {
                                    if (!state.HasLocalVariables)
                                    {
                                        // Wait for pending codes to be fully executed so that "iterations" can be incremented.
                                        // We don't care if this fails, because we need to tidy up in any case
                                        await Codes.Processor.FlushAsync(this);
                                    }

                                    Task varDeletionTask;
                                    using (await LockAsync())
                                    {
                                        Position = state.FilePosition ?? 0;
                                        _parserBuffer.LineNumber = state.LineNumber;
                                        state.ProcessBlock = true;
                                        state.ContinueLoop = false;
                                        state.Iterations++;
                                        varDeletionTask = DeleteLocalVariables(state);
                                        readAgain = true;
                                        if (!IsClosed)
                                        {
                                            _logger.Debug("Restarting {0} block, iterations = {1}", state.Keyword, state.Iterations);
                                        }
                                    }
                                    await varDeletionTask;  // wait outside the code lock to avoid deadlocks
                                    break;
                                }
                                await EndCodeBlock(codeRead ? code.FilePosition ?? Length : Length);
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
                            await EndCodeBlock(codeRead ? code.FilePosition ?? Length : Length);
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
                if (!_codeBlocks.TryPeek(out CodeBlock? codeBlock) || codeBlock.ProcessBlock)
                {
                    // FIXME If/ElseIf/Else/While are not sent to the interceptors
                    if (codeBlock is not null && (code.Keyword != KeywordType.While || code.FilePosition != codeBlock.FilePosition))
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
                                if (_lastCodeBlock is null || _lastCodeBlock.Indent != code.Indent ||
                                    (_lastCodeBlock.Keyword != KeywordType.If && _lastCodeBlock.Keyword != KeywordType.ElseIf))
                                {
                                    throw new CodeParserException("unexpected elif condition", code);
                                }

                                if (!_lastCodeBlock.ExpectingElse)
                                {
                                    // Last if/elif condition was true, ignore the following block
                                    _logger.Debug("Skipping {0} block", code.Keyword);
                                    lock (_codeBlocks)
                                    {
                                        _codeBlocks.Push(new CodeBlock(code, false));
                                    }
                                    break;
                                }
                            }

                            // Start a new conditional block if necessary
                            if (!await Codes.Processor.FlushAsync(this) || IsClosed)
                            {
                                return null;
                            }

                            _logger.Debug("Evaluating {0} block", code.Keyword);
                            if (code.Keyword != KeywordType.While || codeBlock is null || codeBlock.FilePosition != code.FilePosition)
                            {
                                codeBlock = new CodeBlock(code, false);
                                lock (_codeBlocks)
                                {
                                    _codeBlocks.Push(codeBlock);
                                }
                            }

                            // Evaluate the condition
                            string? stringEvaluationResult = await Model.Expressions.Evaluate(code, true);
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
                            if (_lastCodeBlock is null || _lastCodeBlock.Indent != code.Indent ||
                                (_lastCodeBlock.Keyword != KeywordType.If && _lastCodeBlock.Keyword != KeywordType.ElseIf))
                            {
                                throw new CodeParserException("unexpected else", code);
                            }

                            // else condition is true if the last if/elif condition was false
                            _logger.Debug("{0} {1} block", _lastCodeBlock.ExpectingElse ? "Starting" : "Skipping", code.Keyword);
                            lock (_codeBlocks)
                            {
                                _codeBlocks.Push(new CodeBlock(code, _lastCodeBlock.ExpectingElse));
                            }
                            break;

                        case KeywordType.Break:
                        case KeywordType.Continue:
                            if (!_codeBlocks.Any(codeBlock => codeBlock.Keyword == KeywordType.While))
                            {
                                throw new CodeParserException("break or continue cannot be called outside while loop", code);
                            }

                            if (!await Codes.Processor.FlushAsync(this) || IsClosed)
                            {
                                return null;
                            }

                            _logger.Debug("Doing {0}", code.Keyword);
                            foreach (CodeBlock state in _codeBlocks)
                            {
                                state.ProcessBlock = false;
                                if (state.Keyword == KeywordType.While)
                                {
                                    state.ContinueLoop = code.Keyword == KeywordType.Continue;
                                    break;
                                }
                            }
                            break;

                        case KeywordType.Abort:
                            _logger.Debug("Doing {0}", code.Keyword);
                            Close();
                            return code;

                        case KeywordType.Var:
                            ExpectLocalVariable(code, codeBlock);
                            return code;

                        case KeywordType.Skip:
                            // No-op, the rest of the line is ignored
                            break;

                        case KeywordType.None:
                            // The firmware declares the result variable of these codes when it executes them, which may be after their block has been read to its end
                            if (code is { Type: CodeType.MCode, MajorNumber: 260 or 261 } && code.HasParameter('V'))
                            {
                                ExpectLocalVariable(code, codeBlock);
                            }
                            return code;

                        case KeywordType.Echo:
                        case KeywordType.Global:
                        case KeywordType.Set:
                            return code;

                        default:
                            throw new CodeParserException($"Keyword {code.Keyword} is not supported", code);
                    }
                }
            }
        }

        /// <summary>
        /// Make sure the current code block waits for pending codes before it ends because the given code declares a local variable
        /// </summary>
        /// <param name="code">Code declaring the variable</param>
        /// <param name="codeBlock">Current code block or null if there is none</param>
        private void ExpectLocalVariable(Code code, CodeBlock? codeBlock)
        {
            if (codeBlock is null || code.Indent > codeBlock.Indent)
            {
                lock (_codeBlocks)
                {
                    _codeBlocks.Push(new CodeBlock(code, true, KeywordType.Var) { HasLocalVariables = true });
                }
            }
            else
            {
                codeBlock.HasLocalVariables = true;
            }
        }

        /// <summary>
        /// Add a new local variable to the current code block
        /// </summary>
        /// <param name="code">Code declaring the variable</param>
        /// <param name="varName">Name of the variable</param>
        public void AddLocalVariable(Code code, string varName)
        {
            lock (_codeBlocks)
            {
                if (!_codeBlocks.TryPeek(out CodeBlock? codeBlock))
                {
                    // Variables may be declared at file level, in which case they live until the file is closed.
                    // Such a block must use var semantics, else the next code at the same indentation ends it again
                    codeBlock = new CodeBlock(code, true, KeywordType.Var);
                    _codeBlocks.Push(codeBlock);
                }

                if (!codeBlock.LocalVariables.Exists(variable => variable.Name == varName))
                {
                    codeBlock.LocalVariables.Add((varName, code.FilePosition ?? 0));
                }
                codeBlock.HasLocalVariables = true;
            }
        }

        /// <summary>
        /// Get the name under which the firmware keeps a local variable of a closed block.
        /// Loop iterations close the same block again and reuse these names
        /// </summary>
        /// <param name="codeBlock">Closed code block</param>
        /// <param name="index">Index of the local variable</param>
        /// <returns>Variable name without prefix</returns>
        private static string GetHiddenVariableName(CodeBlock codeBlock, int index) => $"dsfClosedBlock{codeBlock.FilePosition}x{index}";

        /// <summary>
        /// Move the local variables of a code block to hidden names so that codes following the block cannot access them
        /// </summary>
        /// <param name="codeBlock">Code block being closed</param>
        /// <param name="varTasks">List of pending variable requests to add to</param>
        private void HideLocalVariables(CodeBlock codeBlock, List<Task> varTasks)
        {
            for (int i = 0; i < codeBlock.LocalVariables.Count; i++)
            {
                // The firmware moves the variables of the File channel to File2 when the file reader is forked, so a variable may be gone
                string name = codeBlock.LocalVariables[i].Name;
                varTasks.Add(SPI.Interface.SetVariable(Channel, true, "var." + GetHiddenVariableName(codeBlock, i), $"exists(var.{name}) ? var.{name} : null"));
                varTasks.Add(SPI.Interface.SetVariable(Channel, false, name, null));
            }
        }

        /// <summary>
        /// Give the hidden local variables of a closed code block their names back.
        /// Variables declared at or past the given file position are dropped because their declarations are executed again
        /// </summary>
        /// <param name="codeBlock">Closed code block being opened again</param>
        /// <param name="position">File position being rewound to</param>
        /// <param name="varTasks">List of pending variable requests to add to</param>
        private void RestoreLocalVariables(CodeBlock codeBlock, long position, List<Task> varTasks)
        {
            for (int i = 0; i < codeBlock.LocalVariables.Count; i++)
            {
                string hiddenName = GetHiddenVariableName(codeBlock, i);
                if (codeBlock.LocalVariables[i].Position < position)
                {
                    varTasks.Add(SPI.Interface.SetVariable(Channel, true, "var." + codeBlock.LocalVariables[i].Name, "var." + hiddenName));
                }
                varTasks.Add(SPI.Interface.SetVariable(Channel, false, hiddenName, null));
            }
            codeBlock.LocalVariables.RemoveAll(variable => variable.Position >= position);
        }

        /// <summary>
        /// Delete the hidden local variables of a closed code block
        /// </summary>
        /// <param name="codeBlock">Closed code block</param>
        /// <param name="varTasks">List of pending variable requests to add to</param>
        private void DeleteHiddenVariables(CodeBlock codeBlock, List<Task> varTasks)
        {
            for (int i = 0; i < codeBlock.LocalVariables.Count; i++)
            {
                varTasks.Add(SPI.Interface.SetVariable(Channel, false, GetHiddenVariableName(codeBlock, i), null));
            }
        }

        /// <summary>
        /// Delete local variables from a given code block
        /// </summary>
        /// <param name="codeBlock">Code block</param>
        /// <returns>Asynchronous task</returns>
        private async Task DeleteLocalVariables(CodeBlock codeBlock)
        {
            Task[] deletionTasks = new Task[codeBlock.LocalVariables.Count];
            for (int i = 0; i < codeBlock.LocalVariables.Count; i++)
            {
                deletionTasks[i] = SPI.Interface.SetVariable(Channel, false, codeBlock.LocalVariables[i].Name, null);
            }
            await Task.WhenAll(deletionTasks);
            codeBlock.LocalVariables.Clear();
            codeBlock.HasLocalVariables = false;
        }

        /// <summary>
        /// Called to finish the current code block
        /// </summary>
        /// <param name="endPosition">File position where the block ended</param>
        /// <returns>Asynchronous task</returns>
        private async Task EndCodeBlock(long endPosition)
        {
            List<Task> varTasks = [];

            using (await LockAsync())
            {
                CodeBlock? codeBlock;
                lock (_codeBlocks)
                {
                    if (!_codeBlocks.TryPop(out codeBlock))
                    {
                        codeBlock = null;
                    }
                }

                if (codeBlock is not null)
                {
                    // Log the end of this block
                    if (codeBlock.Keyword is KeywordType.If or KeywordType.ElseIf or KeywordType.Else or KeywordType.While)
                    {
                        _logger.Debug("End of {0} block", codeBlock.Keyword);
                    }
                    else
                    {
                        _logger.Debug("End of generic block");
                    }

                    if (IsRewindable)
                    {
                        // Keep the block and hide its local variables instead of deleting them. The firmware may still
                        // be executing codes from inside it and a pause can rewind into its scope, in which case the
                        // variables must exist again. RRF cleans up the hidden ones automatically when the file is
                        // closed. Loop iterations re-close the same blocks, so only the latest instance per start
                        // position is kept
                        codeBlock.EndPosition = endPosition;
                        lock (_codeBlocks)
                        {
                            _closedBlocks.RemoveAll(closedBlock =>
                            {
                                if (closedBlock.FilePosition == codeBlock.FilePosition)
                                {
                                    DeleteHiddenVariables(closedBlock, varTasks);
                                    return true;
                                }
                                return false;
                            });
                            HideLocalVariables(codeBlock, varTasks);
                            _closedBlocks.Add(codeBlock);
                        }
                    }
                    else if (codeBlock.HasLocalVariables)
                    {
                        varTasks.Add(DeleteLocalVariables(codeBlock));
                    }

                    // End
                    _lastCodeBlock = codeBlock;
                }
            }

            try
            {
                await Task.WhenAll(varTasks);  // wait outside the code lock to avoid deadlocks
            }
            catch (Exception) when (IsClosed)
            {
                // RRF discards the variables of an aborted file, so requests that were still pending fail
            }
        }
    }
}
