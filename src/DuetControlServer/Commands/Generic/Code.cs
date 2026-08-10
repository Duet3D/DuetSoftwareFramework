using System;
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
    private readonly LinkInterface _linkInterface;
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
    /// <param name="linkInterface">Link interface</param>
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
        LinkInterface linkInterface,
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
        _linkInterface = linkInterface;
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
    /// <param name="linkInterface">Link interface</param>
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
        LinkInterface linkInterface,
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
        _linkInterface = linkInterface;
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

        // Flush the code channel and populate SBC fields where applicable
        if (Keyword == KeywordType.None && _expressions.ContainsSbcFields(this) && !await _codeProcessor.FlushAsync(this, true, false))
        {
            throw new OperationCanceledException();
        }

        // Attempt to process the code internally
        try
        {
            switch (Type)
            {
                case CodeType.GCode:
                    Result = await _gCodes.ProcessAsync(this, CancellationToken);
                    break;
                case CodeType.MCode:
                    Result = await _mCodes.ProcessAsync(this, CancellationToken);
                    break;
                case CodeType.TCode:
                    Result = await _tCodes.ProcessAsync(this, CancellationToken);
                    break;
                case CodeType.Keyword:
                    Result = await _keywords.ProcessAsync(this, CancellationToken);
                    break;
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
#if false // TODO: do we need to do anything now RRF is removed?
                await _linkInterface.SetLastCodeResultAsync(this, CancellationToken);
#endif
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
    /// system directory. It also exposes the code's own parameters to the macro as variables, which
    /// is not done here yet
    /// </remarks>
    private async ValueTask<bool> TryRunCodeMacroAsync()
    {
        if (Type is not (CodeType.GCode or CodeType.MCode) || MajorNumber is null or < 0 or >= 10000)
        {
            return false;
        }

        char letter = Type == CodeType.GCode ? 'G' : 'M';
        string macroName = MinorNumber > 0 ? $"{letter}{MajorNumber}.{MinorNumber}.g" : $"{letter}{MajorNumber}.g";
        // A macro standing in for a code the user typed, so it is not a system macro unless the
        // code that reached here came from one
        return await _macroRunner.TryRunAsync(Channel, macroName, this, isSystemMacro: false,
                                              cancellationToken: CancellationToken);
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
