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
using DuetControlServer.IPC.Processors;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Link.Interface _linkInterface;
    private readonly ICodeHandler _gCodes;
    private readonly ICodeHandler _mCodes;
    private readonly ICodeHandler _tCodes;
    private readonly ICodeHandler _keywords;
    private readonly Settings _settings;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor of a new code
    /// </summary>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Meta G-code expression parser</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="gCodes">G-code handler</param>
    /// <param name="mCodes">M-code handler</param>
    /// <param name="tCodes">T-code handler</param>
    /// <param name="keywords">Keyword handler</param>
    /// <param name="settings">Settings</param>
    public Code(Codes.CodeProcessor codeProcessor,
        Codes.Meta.Expressions expressions,
        Link.Interface linkInterface,
        [FromKeyedServices(Keys.GCodes)] ICodeHandler gCodes,
        [FromKeyedServices(Keys.MCodes)] ICodeHandler mCodes,
        [FromKeyedServices(Keys.TCodes)] ICodeHandler tCodes,
        [FromKeyedServices(Keys.Keywords)] ICodeHandler keywords,
        IOptions<Settings> settings) : base()
    {
        _codeProcessor = codeProcessor;
        _expressions = expressions;
        _linkInterface = linkInterface;
        _gCodes = gCodes;
        _mCodes = mCodes;
        _tCodes = tCodes;
        _keywords = keywords;
        _settings = settings.Value;
    }

    /// <summary>
    /// Constructor of a new code which also parses the given text-based G/M/T-code
    /// </summary>
    /// <param name="code">Text-based G/M/T-code</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Meta G-code expression parser</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="gCodes">G-code handler</param>
    /// <param name="mCodes">M-code handler</param>
    /// <param name="tCodes">T-code handler</param>
    /// <param name="keywords">Keyword handler</param>
    /// <param name="settings">Settings</param>
    public Code(string code,
        Codes.CodeProcessor codeProcessor,
        Codes.Meta.Expressions expressions,
        Link.Interface linkInterface,
        [FromKeyedServices(Keys.GCodes)] ICodeHandler gCodes,
        [FromKeyedServices(Keys.MCodes)] ICodeHandler mCodes,
        [FromKeyedServices(Keys.TCodes)] ICodeHandler tCodes,
        [FromKeyedServices(Keys.Keywords)] ICodeHandler keywords,
        IOptions<Settings> settings) : base(code)
    {
        _codeProcessor = codeProcessor;
        _expressions = expressions;
        _linkInterface = linkInterface;
        _gCodes = gCodes;
        _mCodes = mCodes;
        _tCodes = tCodes;
        _keywords = keywords;
        _settings = settings.Value;
    }

    /// <summary>
    /// Source connection of this command
    /// </summary>
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
        // Assign a cancellation token when the execution starts
        if (CancellationToken == default)
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
            using (File.Lock())
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
    /// <returns>Asynchronous task</returns>
    internal async ValueTask UpdateNextFilePositionAsync()
    {
        if (File is not null && FilePosition is not null)
        {
            using (await File.LockAsync())
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
                _logger.Debug("Writing {0}", this);
                fileWriter.WriteLine(this);
                Result = new();
                return true;
            }
        }

        // Try to process this code internally
        _logger.Debug("Processing {0}", this);

        // Flush the code channel and populate SBC fields where applicable
        if (Keyword == KeywordType.None && _expressions.ContainsSbcFields(this) && !await _linkInterface.FlushAsync(this, true, false))
        {
            throw new OperationCanceledException();
        }

        // Attempt to process the code internally
        try
        {
            switch (Type)
            {
                case CodeType.GCode:
                    Result = await _gCodes.ProcessAsync(this);
                    break;
                case CodeType.MCode:
                    Result = await _mCodes.ProcessAsync(this);
                    break;
                case CodeType.TCode:
                    Result = await _tCodes.ProcessAsync(this);
                    break;
                case CodeType.Keyword:
                    Result = await _keywords.ProcessAsync(this);
                    break;
            }

            if (Result is not null)
            {
                return true;
            }
        }
        catch (Exception e) when (e is MissingParameterException or InvalidParameterTypeException)
        {
            Result = new(MessageType.Error, $"{ToShortString()}: {e.Message}");
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

        // Do not send comments that may not be interpreted by RRF
        if ((Type == CodeType.None) ||
            (Type == CodeType.Comment && (string.IsNullOrWhiteSpace(Comment) || !_settings.FirmwareComments.Any(chunk => Comment.Contains(chunk)))))
        {
            Result = new Message();
            return true;
        }

        // Code has not been interpreted yet - let RRF deal with it
        return false;
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
