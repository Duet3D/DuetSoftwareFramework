using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.IPC;
using DuetControlServer.Files;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Link;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
/// </summary>
public sealed class Code : DuetAPI.Commands.Code, IConnectionCommand
{
    // Private fields
    private readonly Codes.CodeProcessor _codeProcessor;
    private readonly Codes.Meta.Expressions _expressions;
    private readonly GCodeHandler _gCodes;
    private readonly MCodeHandler _mCodes;
    private readonly TCodeHandler _tCodes;
    private readonly KeywordHandler _keywords;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly MacroRunner _macroRunner;
    private readonly ILogger<Code> _logger;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of a new code
    /// </summary>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Meta G-code expression parser</param>
    /// <param name="gCodes">G-code handler</param>
    /// <param name="mCodes">M-code handler</param>
    /// <param name="tCodes">T-code handler</param>
    /// <param name="keywords">Keyword handler</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="macroRunner">Runs macro files</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    public Code(Codes.CodeProcessor codeProcessor,
        Codes.Meta.Expressions expressions,
        [FromKeyedServices(Keys.GCodes)] ICodeHandler gCodes,
        [FromKeyedServices(Keys.MCodes)] ICodeHandler mCodes,
        [FromKeyedServices(Keys.TCodes)] ICodeHandler tCodes,
        [FromKeyedServices(Keys.Keywords)] ICodeHandler keywords,
        IHostApplicationLifetime lifetime,
        MacroRunner macroRunner,
        ILogger<Code> logger,
        IOptions<Settings> settings) : base()
    {
        _codeProcessor = codeProcessor;
        _expressions = expressions;
        _gCodes = (GCodeHandler)gCodes;
        _mCodes = (MCodeHandler)mCodes;
        _tCodes = (TCodeHandler)tCodes;
        _keywords = (KeywordHandler)keywords;
        _lifetime = lifetime;
        _macroRunner = macroRunner;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Constructor of a new code which also parses the given text-based G/M/T-code
    /// </summary>
    /// <param name="code">Text-based G/M/T-code</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Meta G-code expression parser</param>
    /// <param name="gCodes">G-code handler</param>
    /// <param name="mCodes">M-code handler</param>
    /// <param name="tCodes">T-code handler</param>
    /// <param name="keywords">Keyword handler</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="macroRunner">Runs macro files</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    public Code(string code,
        Codes.CodeProcessor codeProcessor,
        Codes.Meta.Expressions expressions,
        [FromKeyedServices(Keys.GCodes)] ICodeHandler gCodes,
        [FromKeyedServices(Keys.MCodes)] ICodeHandler mCodes,
        [FromKeyedServices(Keys.TCodes)] ICodeHandler tCodes,
        [FromKeyedServices(Keys.Keywords)] ICodeHandler keywords,
        IHostApplicationLifetime lifetime,
        MacroRunner macroRunner,
        ILogger<Code> logger,
        IOptions<Settings> settings) : base(code)
    {
        _codeProcessor = codeProcessor;
        _expressions = expressions;
        _gCodes = (GCodeHandler)gCodes;
        _mCodes = (MCodeHandler)mCodes;
        _tCodes = (TCodeHandler)tCodes;
        _keywords = (KeywordHandler)keywords;
        _lifetime = lifetime;
        _macroRunner = macroRunner;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <inheritdoc />
    public Connection? Connection
    {
        get => _connection;
        set
        {
            SourceConnection = value?.Id ?? 0;
            _connection = value;
        }
    }
    private Connection? _connection;

    /// <summary>
    /// Log level for the reply as specified by the firmware via MessageTypeFlags.
    /// Null means no explicit level was set (DSF-internal code), in which case
    /// the default behavior applies (Success: Info, Warning/Error: Warn)
    /// </summary>
    internal EventLogLevel? ReplyLogLevel { get; set; }

    /// <summary>
    /// Cancellation token that may be used to cancel this code
    /// </summary>
    internal CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Used to reset the cancellation token of this code
    /// </summary>
    internal void ResetCancellationToken()
    {
        lock (_codeProcessor.CancellationTokenSources)
        {
            CancellationToken = _codeProcessor.CancellationTokenSources[(int)Channel].Token;
        }
    }

    /// <summary>
    /// Run an arbitrary G/M/T-code and wait for it to finish or to be enqueued if it is asynchronous
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the code</returns>
    /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
    public override async Task<Message?> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Assign a cancellation token when the execution starts.
        // Prioritized codes use the application stopping token so they survive channel resets (e.g. emergency stop)
        if (cancellationToken != default)
        {
            CancellationToken = cancellationToken;
        }
        else if (Flags.HasFlag(CodeFlags.IsPrioritized))
        {
            CancellationToken = _lifetime.ApplicationStopping;
        }
        else
        {
            CancellationToken = _codeProcessor.CancellationTokenSources[(int)Channel].Token;
        }

        // Send it to the code pipeline
        await _codeProcessor.StartCodeAsync(this);

        // Wait for the result unless it has the asynchronous flag
        if (!Flags.HasFlag(CodeFlags.Asynchronous))
        {
            await Task;
            return Result;
        }
        return null;
    }

    /// <summary>
    /// Current stage of this code on the code pipeline
    /// </summary>
    internal Codes.PipelineStage? Stage { get; set; }

    /// <summary>
    /// File that started this code
    /// </summary>
    internal Files.CodeFile? File { get; set; }

    /// <summary>
    /// Whether the ProcessInternally worker is deferring this code: the channel continues past it,
    /// and its handler runs when its anchor move has retired
    /// </summary>
    internal bool IsCurrentlyDeferred { get; set; }

    /// <summary>
    /// Ring the anchor move was queued on
    /// </summary>
    internal int DeferredRing { get; set; }

    /// <summary>
    /// Id of the anchor: the last move submitted on the channel's ring when this code was read.
    /// The code's effect belongs after the end of that move
    /// </summary>
    internal uint DeferredAnchor { get; set; }

    /// <summary>
    /// Completion of the previously deferred code on the same pipeline, or null if there is none
    /// pending. Handlers of deferred codes must run in file order even when they share an anchor,
    /// and the anchor wait alone does not order their wakes
    /// </summary>
    internal Task? DeferredPredecessor { get; set; }

    /// <summary>
    /// The handler this code's type routes to, or null if none does
    /// </summary>
    private ICodeHandler? InternalHandler => Type switch
    {
        CodeType.GCode => _gCodes,
        CodeType.MCode => _mCodes,
        CodeType.TCode => _tCodes,
        CodeType.Keyword => _keywords,
        _ => null
    };

    /// <summary>
    /// Classify this code through the handler its type routes to
    /// </summary>
    /// <returns>The declared class, or null if no handler implements the code</returns>
    internal Codes.CodeClass? ClassifyInternally() => InternalHandler?.Classify(this);

    /// <summary>
    /// Update the next file position in case we need to fork this file
    /// </summary>
    internal void UpdateNextFilePosition()
    {
        if (File is not null && FilePosition is not null)
        {
            using (File.Lock(CancellationToken))
            {
                long nextFilePosition = FilePosition.Value + (Length ?? 0L);
                if (File.NextFilePosition < nextFilePosition)
                {
                    File.NextFilePosition = nextFilePosition;
                }
            }
        }
    }

    /// <summary>
    /// Update the next file position in case we need to fork this file
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    internal async ValueTask UpdateNextFilePositionAsync(CancellationToken cancellationToken)
    {
        if (File is not null && FilePosition is not null)
        {
            using (await File.LockAsync(cancellationToken))
            {
                long nextFilePosition = FilePosition.Value + (Length ?? 0L);
                if (File.NextFilePosition < nextFilePosition)
                {
                    File.NextFilePosition = nextFilePosition;
                }
            }
        }
    }

    /// <summary>
    /// Indicates if this is a comment or empty code that is not interpreted by RepRapFirmware
    /// </summary>
    /// <remarks>
    /// Such codes are resolved internally and never sent to the firmware, so a print cannot be paused at them
    /// </remarks>
    internal bool IsNonFirmwareComment => (Type == CodeType.None) ||
        (Type == CodeType.Comment && (string.IsNullOrWhiteSpace(Comment) || !_settings.FirmwareComments.Any(chunk => Comment.Contains(chunk))));

    /// <summary>
    /// Attempt to process this code internally
    /// </summary>
    /// <returns>Whether the code could be processed internally</returns>
    /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
    internal async ValueTask<bool> ProcessInternally()
    {
        if (Keyword != KeywordType.None &&
            Keyword != KeywordType.Echo &&
            Keyword != KeywordType.Abort &&
            Keyword != KeywordType.Global &&
            Keyword != KeywordType.Var &&
            Keyword != KeywordType.Set)
        {
            // Other meta keywords will be handled later
            throw new InvalidOperationException("Conditional codes must not be executed");
        }

        // Check if this code is supposed to be written to a file
        int numChannel = (int)Channel;
        using (await _codeProcessor.FileLocks[numChannel].LockAsync())
        {
            TextWriter? fileWriter = _codeProcessor.FilesBeingWritten[numChannel];
            if (fileWriter is not null && (Type != CodeType.MCode || MajorNumber != 29))
            {
                _logger.LogDebug("Writing {Code}", this);
                fileWriter.WriteLine(this);
                Result = new();
                return true;
            }
        }

        // Try to process this code internally
        _logger.LogDebug("Processing {Code}", this);

        // An expression reading the object model must not be evaluated while earlier codes are
        // still completing, so such a code waits for them first; the flush evaluates the
        // expressions once the state has settled. One referencing only variables needs no wait,
        // because this stage runs codes in stream order, but its parameters still have to be
        // evaluated here: the handlers read numeric parameters only
        if (Keyword == KeywordType.None)
        {
            if (_expressions.ContainsModelFields(this))
            {
                if (!await _codeProcessor.FlushAsync(this, evaluateExpressions: true, cancellationToken: CancellationToken))
                {
                    throw new OperationCanceledException();
                }
            }
            else
            {
                await _expressions.EvaluateAsync(this, CancellationToken);
            }
        }

        // Attempt to process the code internally. The handler declares each code's class in its
        // table; the class's synchronisation runs here, before the handler sees the code, so "does
        // this code need a standstill" is a declared fact rather than a call the handler remembers
        // to make. A code its handler does not classify has no row: no handler runs, and the code
        // goes down the macro-then-unsupported path below
        try
        {
            ICodeHandler? handler = InternalHandler;
            if (handler is not null && handler.Classify(this) is Codes.CodeClass codeClass)
            {
                // A prioritized code jumps every queue by definition
                if (!Flags.HasFlag(CodeFlags.IsPrioritized))
                {
                    switch (codeClass)
                    {
                        case Codes.CodeClass.Flush:
                            // The move carries the value; the flush keeps evaluation order
                            if (!await _codeProcessor.FlushAsync(this, cancellationToken: CancellationToken))
                            {
                                throw new OperationCanceledException();
                            }
                            break;
                        case Codes.CodeClass.FlushAndStandstill:
                            // The code changes what a queued move means, or needs the board's
                            // reply: nothing may be moving when the handler runs
                            if (!await _codeProcessor.FlushAsync(this, cancellationToken: CancellationToken) ||
                                !await _codeProcessor.WaitForStandstillAsync(CancellationToken))
                            {
                                throw new OperationCanceledException();
                            }
                            break;
                        case Codes.CodeClass.Deferred:
                            // The effect belongs at a point in the path. A currently deferred
                            // code was flushed by the worker beforehand, so its parameters are
                            // frozen; it holds its handler back until its anchor move has
                            // retired, after the deferred code before it so that effects land in
                            // file order even when they share an anchor. One not being deferred
                            // (no move in flight, or not from the job) flushes and applies now
                            if (IsCurrentlyDeferred)
                            {
                                if (DeferredPredecessor is not null)
                                {
                                    await DeferredPredecessor;
                                }
                                if (DeferredAnchor != 0 &&
                                    !await _codeProcessor.WaitForRetirementAsync(DeferredRing, DeferredAnchor, CancellationToken))
                                {
                                    // A stop dropped the move this code's effect belongs after, so
                                    // the point in the path it was waiting for will never be
                                    // reached. The rewind re-reads its line, which is what makes it
                                    // fire exactly once
                                    throw new OperationCanceledException();
                                }
                            }
                            else if (!await _codeProcessor.FlushAsync(this, cancellationToken: CancellationToken))
                            {
                                throw new OperationCanceledException();
                            }
                            break;
                    }
                }
                Result = await handler.ProcessAsync(this, CancellationToken);
            }

            if (Result is not null)
            {
                return true;
            }
        }
        catch (Exception e) when (e is NotSupportedException)
        {
            ResolveAsUnsupported();
            return true;
        }
        catch (Exception e) when (e is GCodeException or MissingParameterException or InvalidParameterTypeException)
        {
            Result = new(MessageType.Error, e.Message);
            return true;
        }

        // If the code could not be interpreted internally, post-process it
        if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
        {
            bool resolved = await CodeInterception.InterceptAsync(this, InterceptionMode.Post);

            Flags |= CodeFlags.IsPostProcessed;
            if (resolved)
            {
                return true;
            }
        }

        // A comment carries no instruction, so there is nothing left to interpret
        if (IsNonFirmwareComment)
        {
            Result = new Message();
            return true;
        }

        // No handler recognised this code, so try a macro named after it. This is how a machine adds
        // a code of its own in RepRapFirmware - M1234 runs sys/M1234.g - and it has to be tried
        // before the code is called unsupported, or those machines stop working
        if (await TryRunCodeMacroAsync())
        {
            Result ??= new Message();
            return true;
        }

        ResolveAsUnsupported();
        return true;
    }

    /// <summary>
    /// Run the macro file named after this code, if there is one
    /// </summary>
    /// <returns>True if such a macro existed and was run</returns>
    /// <remarks>
    /// RepRapFirmware looks for <c>&lt;letter&gt;&lt;number&gt;.g</c>, or
    /// <c>&lt;letter&gt;&lt;number&gt;.&lt;fraction&gt;.g</c> for a code with a fraction, in the
    /// system directory, and gives the macro the code's own parameters, so that <c>M1234 X5</c> can
    /// read <c>param.X</c>
    /// </remarks>
    private async ValueTask<bool> TryRunCodeMacroAsync()
    {
        if (Type is not (CodeType.GCode or CodeType.MCode) || MajorNumber is null or < 0 or >= 10000)
        {
            return false;
        }

        char letter = Type == CodeType.GCode ? 'G' : 'M';
        string macroName = MinorNumber > 0 ? $"{letter}{MajorNumber}.{MinorNumber}.g" : $"{letter}{MajorNumber}.g";

        // The code's own parameters, by letter, keeping the type the parser gave each one so that
        // param.S is a string and param.X is a number
        // TODO: pass array parameters too, once a variable can hold an array
        Dictionary<string, object?> parameters = [];
        foreach (CodeParameter parameter in Parameters)
        {
            object? value =
                parameter.IsNull ? null :
                parameter.Type == typeof(string) ? (string?)parameter :
                parameter.Type == typeof(int) ? (int)parameter :
                parameter.Type == typeof(uint) ? (uint)parameter :
                parameter.Type == typeof(long) ? (long)parameter :
                parameter.Type == typeof(float) ? (float)parameter :
                null;
            if (value is not null || parameter.IsNull)
            {
                parameters[parameter.Letter.ToString()] = value;
            }
        }

        // A macro standing in for a code the user typed, so it is not a system macro unless the
        // code that reached here came from one
        return await _macroRunner.TryRunAsync(Channel, macroName, this, isSystemMacro: false,
                                              parameters: parameters, cancellationToken: CancellationToken);
    }

    /// <summary>
    /// Resolve this code as one nothing supports
    /// </summary>
    /// <remarks>
    /// There used to be a firmware behind DuetControlServer that unrecognised codes were passed to,
    /// and "no handler here" meant "let RepRapFirmware try". It no longer does: a code is either
    /// executed here or it is not executed at all. The wording and the severity match what
    /// RepRapFirmware replied in the same situation - a warning, not an error - because macros and
    /// user interfaces have been reading it for years
    /// </remarks>
    internal void ResolveAsUnsupported()
    {
        Result ??= new Message(MessageType.Warning, $"{ToShortString()}: Command is not supported");
    }

    /// <summary>
    /// Size of this code in binary representation
    /// </summary>
    internal int BinarySize { get; set; }

    /// <summary>
    /// Task to complete when this code is complete
    /// </summary>
    internal Task<Message?> Task => _tcs.Task;

    /// <summary>
    /// Set this code as complete
    /// </summary>
    public void SetFinished() => _tcs.TrySetResult(Result);

    /// <summary>
    /// Set this code as cancelled
    /// </summary>
    public void SetCancelled() => _tcs.TrySetCanceled();

    /// <summary>
    /// Set an exception for this code
    /// </summary>
    /// <param name="e">Exception to set</param>
    public void SetException(Exception e) => _tcs.TrySetException(e);

    /// <summary>
    /// Internal TCS representing the lifecycle of a code
    /// </summary>
    private TaskCompletionSource<Message?> _tcs = new();

    /// <summary>
    /// Resets more <see cref="Code"/> fields
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        Connection = null;
        Stage = Codes.PipelineStage.Start;
        File = null;
        _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BinarySize = 0;
    }
}
