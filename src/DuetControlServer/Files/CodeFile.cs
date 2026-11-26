using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Link;
using DuetControlServer.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files;

/// <summary>
/// Class to read G/M/T-codes from files
/// </summary>
/// <param name="filePath">Path of the file</param>
/// <param name="channel">Channel to send the codes to</param>
/// <param name="codeFactory">Factory to create new codes</param>
/// <param name="codeProcessor">Code processor to process the codes</param>
/// <param name="expressions">Expressions to evaluate the codes</param>
/// <param name="model">Object model to access the machine state</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="settings">Settings to use</param>
public class CodeFile(
    CodeFilePath filePath,
    CodeChannel channel,
    CodeFactory codeFactory,
    CodeProcessor codeProcessor,
    Expressions expressions,
    Model.ObjectModel model,
    ILoggerFactory loggerFactory,
    IOptions<Settings> settings) : IDisposable
{
    // Private fields
    private readonly ILogger<CodeFile> logger = loggerFactory.CreateLogger<CodeFile>();

    /// <summary>
    /// Internal lock
    /// </summary>
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Lock this instance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock</returns>
    public IDisposable Lock(CancellationToken cancellationToken) => _lock.Lock(cancellationToken);

    /// <summary>
    /// Lock this instance asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock</returns>
    public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken) => _lock.LockAsync(cancellationToken);

    /// <summary>
    /// File being read from
    /// </summary>
    private readonly FileStream _fileStream = new(filePath.Physical, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);

    /// <summary>
    /// Internal buffer used for reading from files
    /// </summary>
    private readonly CodeParserBuffer _parserBuffer = new(settings.Value.FileBufferSize, true);

    /// <summary>
    /// Current line number in the file
    /// </summary>
    public long LineNumber => _parserBuffer.LineNumber ?? 0;

    /// <summary>
    /// File path to the file being executed
    /// </summary>
    public CodeFilePath FilePath { get; } = filePath;

    /// <summary>
    /// Channel to send the codes to
    /// </summary>
    public CodeChannel Channel { get; } = channel;

    /// <summary>
    /// Innermost open code block of the conditional G-code being read
    /// </summary>
    /// <remarks>Written by the reader, and read by the codes that ask for the iterations</remarks>
    private volatile CodeBlock? _codeBlock;

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
            }
        }
    }

    /// <summary>
    /// State to read on from the current position in
    /// </summary>
    internal CodeFileState State
    {
        get
        {
            long position = Position;
            return new CodeFileState(position, position, _parserBuffer.State, _parserBuffer.State, _codeBlock, _lastCodeBlock, Variables.Snapshot());
        }
    }

    /// <summary>
    /// Read from the code the given state is of
    /// </summary>
    /// <param name="state">State of the code</param>
    /// <param name="atEnd">Whether to read from the end of the code rather than from its start</param>
    /// <remarks>
    /// This instance must be locked when this is called
    /// </remarks>
    internal void Rewind(CodeFileState state, bool atEnd)
    {
        if (!IsClosed)
        {
            Position = atEnd ? state.EndPosition : state.StartPosition;
            _parserBuffer.State = atEnd ? state.End : state.Start;
            _codeBlock = state.Block;
            _lastCodeBlock = state.LastBlock;
            Variables.Restore(state.Variables ?? ImmutableDictionary<string, LocalVariable>.Empty);
        }
    }

    /// <summary>
    /// Whether this file is on its first command since a restart
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodeMachineState::firstCommandAfterRestart</c>. A resume after a pause
    /// that abandoned macros sets it on the job file, as does starting a job from a saved position.
    /// A macro started while the invoking file holds it inherits it, which is what
    /// <c>state.macroRestarted</c> is computed from, and it is cleared once the file's first
    /// command has executed, so only the command at the restart point ever carries it
    /// </remarks>
    public bool FirstCommandAfterRestart { get; set; }

    /// <summary>
    /// The G-code number a line that names no command letter repeats, or -1 if there is none
    /// </summary>
    /// <remarks>Set by <c>M26 C</c>, RepRapFirmware's <c>SetModalGCommand</c>. A rewind puts it back with the rest of the parser state</remarks>
    public int ModalGCommand
    {
        get => _parserBuffer.LastGCode;
        set => _parserBuffer.LastGCode = value;
    }

    /// <summary>
    /// Get the current number of iterations of the current loop
    /// </summary>
    /// <param name="code">Code that requested the number of iterations</param>
    /// <returns>Number of iterations</returns>
    /// <exception cref="CodeParserException">Query came outside a while loop</exception>
    public int GetIterations(Code code)
    {
        return _codeBlock?.Loop?.Iterations ?? throw new CodeParserException("'iterations' used when not inside a loop", code);
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
    /// Whether the codes of this file are to be held at the dispatch barrier
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pause that has to wait for a macro to finish arms this on the job file. The pipeline
    /// consults it beside the cancellation check it already makes, so a code whose stack level is
    /// this file is cancelled instead of being processed - which puts the barrier in the dispatch
    /// path itself rather than in something that has to poll for the macro ending. The macro's own
    /// codes are a child stack item and are not held, so the macro runs to its end.
    /// </para>
    /// <para>
    /// RepRapFirmware makes the same check in <c>StartNextGCode</c>, before it starts the next
    /// command from the file. Cancelling rather than holding is what keeps a flush of this level
    /// from waiting on the barrier, and the codes it cancels are the ones the rewind re-reads
    /// </para>
    /// </remarks>
    public bool HoldAtNextCode
    {
        get => _holdAtNextCode;
        set => _holdAtNextCode = value;
    }
    private volatile bool _holdAtNextCode;

    /// <summary>
    /// Close this file
    /// </summary>
    public void Close() => _isClosed = true;

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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Read code or null if none found</returns>
    /// <exception cref="CodeParserException">Failed to read the next code</exception>
    /// <exception cref="OperationCanceledException">Failed to flush the pending codes</exception>
    /// <remarks>
    /// This instance must NOT be locked when this is called
    /// </remarks>
    public async ValueTask<Code?> ReadCodeAsync(Code? sharedCode = null, CancellationToken cancellationToken = default)
    {
        Code code = sharedCode ?? codeFactory.Create();
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
            CodeParserState startOfCode;
            using (await LockAsync(cancellationToken))
            {
                if (IsClosed)
                {
                    return null;
                }

                do
                {
                    // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        _parserBuffer.MayRepeatCode = model.State.MachineMode is MachineMode.CNC or MachineMode.Laser;
                    }

                    // Get the next code
                    startOfCode = _parserBuffer.State;
                    codeRead = await DuetAPI.Commands.Code.ParseAsync(_fileStream, code, _parserBuffer, cancellationToken);
                }
                while (!codeRead && _parserBuffer.GetPosition(_fileStream) < _fileStream.Length);

                if (codeRead)
                {
                    logger.LogReadCode(code);
                }
            }

            // Check if this is the end of the last block(s)
            bool readAgain = false;
            while (_codeBlock is CodeBlock state)
            {
                if (!codeRead || (code.Type != CodeType.Comment && state.IsFinished(code.Indent)))
                {
                    // Blocks the end of the file would close are left standing, unless an enclosing
                    // loop is about to run the file again: the machine may still be executing codes
                    // that were read ahead, and a pause in that window rewinds the file into them, so
                    // the local variables they reference must stay resolvable. The blocks die with
                    // this instance instead, which is when RepRapFirmware deletes them too: its
                    // StopPrint clears the job file's blocks, not the read-ahead reaching the end of
                    // the file
                    if (!codeRead && !state.IsInsideActiveLoop)
                    {
                        break;
                    }

                    // Wait for pending commands to be executed so all the local variables can be disposed of again
                    if (state.HasLocalVariables && !await FlushAsync(cancellationToken))
                    {
                        return null;
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
                                // Wait for pending codes to be fully executed so that "iterations" can be incremented
                                if (!state.HasLocalVariables && !await FlushAsync(cancellationToken))
                                {
                                    return null;
                                }

                                using (await LockAsync(cancellationToken))
                                {
                                    Position = state.FilePosition ?? 0;
                                    _parserBuffer.LineNumber = state.LineNumber;
                                    DeleteLocalVariables(state);
                                    _codeBlock = state with
                                    {
                                        ProcessBlock = true,
                                        ContinueLoop = false,
                                        Iterations = state.Iterations + 1,
                                        HasLocalVariables = false
                                    };
                                    readAgain = true;
                                    if (!IsClosed)
                                    {
                                        logger.LogRestartingBlock(state.Keyword, state.Iterations + 1);
                                    }
                                }
                                break;
                            }
                            EndCodeBlock(state);
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
                        EndCodeBlock(state);
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
            CodeBlock? codeBlock = _codeBlock;
            if (codeBlock is null || codeBlock.ProcessBlock)
            {
                // FIXME If/ElseIf/Else/While are not sent to the interceptors
                if (codeBlock is { SeenCodes: false } && (code.Keyword != KeywordType.While || code.FilePosition != codeBlock.FilePosition))
                {
                    _codeBlock = codeBlock = codeBlock with { SeenCodes = true };
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
                                logger.LogDebug("Skipping {Keyword} block", code.Keyword);
                                _codeBlock = new CodeBlock(code, false, codeBlock);
                                break;
                            }
                        }

                        // Start a new conditional block if necessary
                        if (!await FlushAsync(cancellationToken))
                        {
                            return null;
                        }

                        logger.LogDebug("Evaluating {Keyword} block", code.Keyword);
                        if (code.Keyword != KeywordType.While || codeBlock is null || codeBlock.FilePosition != code.FilePosition)
                        {
                            _codeBlock = codeBlock = new CodeBlock(code, false, codeBlock);
                        }

                        // Evaluate the condition
                        string? stringEvaluationResult = await expressions.EvaluateAsync(code, cancellationToken);
                        if (bool.TryParse(stringEvaluationResult, out bool evaluationResult))
                        {
                            logger.LogDebug("Evaluation result: ({KeywordArgument}) = {Result}", code.KeywordArgument, evaluationResult);
                            _codeBlock = codeBlock with
                            {
                                ProcessBlock = evaluationResult,
                                ExpectingElse = (code.Keyword != KeywordType.While) && !evaluationResult
                            };
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
                        logger.LogDebug("{Operation} {Keyword} block", _lastCodeBlock.ExpectingElse ? "Starting" : "Skipping", code.Keyword);
                        _codeBlock = new CodeBlock(code, _lastCodeBlock.ExpectingElse, codeBlock);
                        break;

                    case KeywordType.Break:
                    case KeywordType.Continue:
                        if (codeBlock?.Loop is null)
                        {
                            throw new CodeParserException("break or continue cannot be called outside while loop", code);
                        }

                        if (!await FlushAsync(cancellationToken))
                        {
                            return null;
                        }

                        logger.LogDebug("Doing {Keyword}", code.Keyword);
                        _codeBlock = codeBlock.Leave(code.Keyword == KeywordType.Continue);
                        break;

                    case KeywordType.Abort:
                        logger.LogDebug("Doing {Keyword}", code.Keyword);
                        Close();
                        return code;

                    case KeywordType.Var:
                        if (codeBlock is null || code.Indent > codeBlock.Indent)
                        {
                            _codeBlock = new CodeBlock(code, true, codeBlock) { HasLocalVariables = true };
                        }
                        else if (!codeBlock.HasLocalVariables)
                        {
                            _codeBlock = codeBlock with { HasLocalVariables = true };
                        }
                        SaveState(code, startOfCode);
                        return code;

                    case KeywordType.Skip:
                        // No-op, the rest of the line is ignored
                        break;

                    case KeywordType.Echo:
                    case KeywordType.Global:
                    case KeywordType.None:
                    case KeywordType.Set:
                        SaveState(code, startOfCode);
                        return code;

                    default:
                        throw new CodeParserException($"Keyword {code.Keyword} is not supported", code);
                }
            }
        }
    }

    /// <summary>
    /// Wait for the codes read from this file to be executed
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// False if reading has been stopped, so that some of the codes may have been cancelled. They are going to be read again
    /// </returns>
    private async ValueTask<bool> FlushAsync(CancellationToken cancellationToken)
    {
        if (!await codeProcessor.FlushAsync(this, cancellationToken))
        {
            return false;
        }
        await WaitWhileHeldAsync();
        return !IsClosed && !HoldAtNextCode && !cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Wait while a pause holds this file where it is
    /// </summary>
    /// <returns>Asynchronous task</returns>
    internal virtual ValueTask WaitWhileHeldAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Record the state a code was read in
    /// </summary>
    /// <param name="code">Code that has just been read</param>
    /// <param name="startOfCode">State of the parser where it starts</param>
    private void SaveState(Code code, CodeParserState startOfCode)
    {
        long start = code.FilePosition ?? 0L;
        code.FileState = new CodeFileState(start, start + (code.Length ?? 0L), startOfCode, _parserBuffer.State, _codeBlock, _lastCodeBlock, null);
    }

    /// <summary>
    /// Variables this file has created and the parameters it was called with
    /// </summary>
    /// <remarks>
    /// A file gets its own set, so a macro cannot see the variables of whatever started it. The block
    /// a variable was created in decides how long it lives; the file decides who can see it
    /// </remarks>
    public VariableSet Variables { get; } = new();

    /// <summary>
    /// Delete the local variables a code block created
    /// </summary>
    /// <param name="codeBlock">Code block that is ending or restarting</param>
    private void DeleteLocalVariables(CodeBlock codeBlock)
    {
        if (codeBlock.HasLocalVariables)
        {
            Variables.DeleteVariables(codeBlock.Depth);
        }
    }

    /// <summary>
    /// Called to finish the current code block
    /// </summary>
    /// <param name="codeBlock">The current code block</param>
    private void EndCodeBlock(CodeBlock codeBlock)
    {
        // Log the end of this block
        if (codeBlock.Keyword is KeywordType.If or KeywordType.ElseIf or KeywordType.Else or KeywordType.While)
        {
            logger.LogDebug("End of {Keyword} block", codeBlock.Keyword);
        }
        else
        {
            logger.LogDebug("End of generic block");
        }

        // Delete previously created local variables
        DeleteLocalVariables(codeBlock);

        // End
        _codeBlock = codeBlock.Parent;
        _lastCodeBlock = codeBlock;
    }
}
