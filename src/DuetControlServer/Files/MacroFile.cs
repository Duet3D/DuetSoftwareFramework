using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Link;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files;

/// <summary>
/// Class representing a macro being executed
/// </summary>
public sealed class MacroFile : CodeFile, IDisposable
{
    // Private fields
    private readonly CodeFactory _codeFactory;
    private readonly CodeProcessor _codeProcessor;
    private readonly Utility.Logger _dsfLogger;
    private readonly Model.ObjectModel _model;
    private IHostApplicationLifetime _lifetime;
    private Settings _settings;

    /// <summary>
    /// Static logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// IPC connection that (indirectly) requested this macro file
    /// </summary>
    public int SourceConnection { get; }

    /// <summary>
    /// Indicates if this macro was started from a G/M/T-code
    /// </summary>
    public bool IsNested { get; }

    /// <summary>
    /// Indicates if this macro can be aborted on a pause request
    /// </summary>
    public bool IsPausable { get; set; }

    /// <summary>
    /// Internal cancellation token source used for codes
    /// </summary>
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// Cancellation token that is triggered when the file is cancelled/aborted
    /// </summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Whether this file is config.g or config.g.bak
    /// </summary>
    public bool IsConfig { get; }

    /// <summary>
    /// Whether this file is config-override.g
    /// </summary>
    public bool IsConfigOverride { get; }

    /// <summary>
    /// Whether this file is dsf-config.g
    /// </summary>
    public bool IsDsfConfig { get; }

    /// <summary>
    /// Indicates if the macro was ever started
    /// </summary>
    public bool WasStarted { get; private set; }

    /// <summary>
    /// Indicates if the macro file has just started
    /// </summary>
    public bool JustStarted { get; set; }

    /// <summary>
    /// Indicates if the macro file is being executed
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        set => _isExecuting = value;
    }
    private volatile bool _isExecuting;

    /// <summary>
    /// Indicates if the macro file has been aborted
    /// </summary>
    public bool IsAborted { get; private set; }

    /// <summary>
    /// Constructor of a macro
    /// </summary>
    /// <param name="fileName">Filename of the macro</param>
    /// <param name="physicalFile">Physical path of the macro</param>
    /// <param name="channel">Code requesting the macro</param>
    /// <param name="startCode">Code starting the macro file</param>
    /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
    /// <param name="codeFactory">Code factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="dsfLogger">Duet Software Framework logger</param>
    /// <param name="expressions">Expression evaluator</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="model">Object model</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="settings">Settings</param>
    private MacroFile(string fileName, string physicalFile, CodeChannel channel, Code? startCode, int sourceConnection,
        CodeFactory codeFactory, CodeProcessor codeProcessor, Utility.Logger dsfLogger, Expressions expressions, LinkInterface linkInterface, Model.ObjectModel model, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
        : base(fileName, physicalFile, channel, codeFactory, codeProcessor, expressions, linkInterface, model, lifetime, settings)
    {
        SourceConnection = sourceConnection;

        _codeFactory = codeFactory;
        _codeProcessor = codeProcessor;
        _dsfLogger = dsfLogger;
        _model = model;
        _settings = settings.Value;
        _lifetime = lifetime;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

        // Are we executing config.g, config-override.g, or dsf-config.g?
        if (startCode is not null)
        {
            IsNested = true;
            IsConfigOverride = startCode is { Type: CodeType.MCode, MajorNumber: 501 } && (fileName == FilePathResolver.ConfigOverrideFile);
            IsDsfConfig = fileName == FilePathResolver.DsfConfigFile;
        }
        else if (physicalFile == Path.Combine(settings.Value.BaseDirectory, "sys", FilePathResolver.ConfigFile) ||
                    physicalFile == Path.Combine(settings.Value.BaseDirectory, "sys", FilePathResolver.ConfigFileFallback))
        {
            IsConfig = true;
        }
    }

    /// <summary>
    /// Copy constructor
    /// </summary>
    /// <param name="copyFrom">File to copy from</param>
    /// <param name="channel">Code channel to assign</param>
    /// <param name="codeFactory">Code factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Expression evaluator</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="model">Object model</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="settings">Settings</param>
    public MacroFile(MacroFile copyFrom, CodeChannel channel, CodeProcessor codeProcessor, CodeFactory codeFactory, Expressions expressions, LinkInterface linkInterface, Model.ObjectModel model, IHostApplicationLifetime lifetime, IOptions<Settings> settings) : base(copyFrom, channel, codeFactory, codeProcessor, expressions, linkInterface, model, lifetime, settings)
    {
        SourceConnection = copyFrom.SourceConnection;
        IsNested = copyFrom.IsNested;
        IsPausable = copyFrom.IsPausable;
        IsConfig = copyFrom.IsConfig;
        IsConfigOverride = copyFrom.IsConfigOverride;
        IsDsfConfig = copyFrom.IsDsfConfig;
        IsAborted = copyFrom.IsAborted;

        _codeFactory = copyFrom._codeFactory;
        _codeProcessor = copyFrom._codeProcessor;
        _dsfLogger = copyFrom._dsfLogger;
        _model = copyFrom._model;
        _settings = copyFrom._settings;
        _lifetime = copyFrom._lifetime;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
    }

    /// <summary>
    /// Start executing this macro file in the background
    /// </summary>
    public void Start(bool notifyFirmware = true)
    {
        if (!IsAborted)
        {
            WasStarted = IsExecuting = true;
            JustStarted = notifyFirmware;
            Task.Run(RunAsync);
        }
    }

    /// <summary>
    /// Abort this macro
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public void Abort()
    {
        if (IsAborted || _disposed)
        {
            return;
        }
        IsAborted = true;
        _cts.Cancel();

        Close();
        _logger.Info("Aborted macro file {0}", FileName);
    }

    /// <summary>
    /// Internal TCS to resolve when the macro has finished
    /// </summary>
    private TaskCompletionSource? _finishTcs;

    /// <summary>
    /// Wait for this macro to finish asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// This task is always resolved and never cancelled
    /// </remarks>
    public Task WaitForFinishAsync()
    {
        if (!IsExecuting)
        {
            return Task.CompletedTask;
        }

        if (_finishTcs is not null)
        {
            return _finishTcs.Task;
        }
        _finishTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _finishTcs.Task;
    }

    /// <summary>
    /// Extra steps to perform before config.g is processed
    /// </summary>
    private enum ConfigExtraSteps
    {
        SendHostname,
        SendDateTime,
        Done
    }

    /// <summary>
    /// Current extra step being performed (provided config.g is being executed)
    /// </summary>
    private ConfigExtraSteps _extraConfigStep = ConfigExtraSteps.SendHostname;

    /// <summary>
    /// Read the next available code asynchronously
    /// </summary>
    /// <returns>Read code</returns>
    private async Task<Code?> ReadCodeAsync()
    {
        Code? result;

        // When executing config.g, perform some extra steps...
        if (IsConfig)
        {
            switch (_extraConfigStep)
            {
                case ConfigExtraSteps.SendHostname:
                    result = _codeFactory.Create();
                    result.Channel = Channel;
                    result.File = this;
                    result.Flags = CodeFlags.IsInternallyProcessed;        // don't check our own hostname
                    result.Type = CodeType.MCode;
                    result.MajorNumber = 550;
                    result.Parameters.Add(new CodeParameter('P', Environment.MachineName));
                    _extraConfigStep = ConfigExtraSteps.SendDateTime;
                    break;

                case ConfigExtraSteps.SendDateTime:
                    result = _codeFactory.Create();
                    result.Channel = Channel;
                    result.File = this;
                    result.Flags = CodeFlags.IsInternallyProcessed;        // don't check our own datetime
                    result.Type = CodeType.MCode;
                    result.MajorNumber = 905;
                    result.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                    result.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                    _extraConfigStep = ConfigExtraSteps.Done;
                    break;

                default:
                    result = await base.ReadCodeAsync();
                    break;
            }
        }
        else
        {
            result = await base.ReadCodeAsync();
        }

        // Update code information
        if (result is not null)
        {
            result.CancellationToken = CancellationToken;
            result.Flags |= CodeFlags.Asynchronous | CodeFlags.IsFromMacro;
            if (IsConfig) { result.Flags |= CodeFlags.IsFromConfig; }
            if (IsConfigOverride) { result.Flags |= CodeFlags.IsFromConfigOverride; }
            if (IsNested) { result.Flags |= CodeFlags.IsNestedMacro; }
            result.SourceConnection = SourceConnection;
            return result;
        }

        // File has finished
        return null;
    }

    /// <summary>
    /// Method representing the lifecycle of a macro being executed
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private async Task RunAsync()
    {
        // Reset start-up error
        if (IsConfig)
        {
            using (await _model.AccessReadWriteAsync())
            {
                _model.State.StartupError = null;
            }
        }

        // Check if we're executing a config file
        bool executingConfigFile = false;
        if (IsConfig || IsConfigOverride || IsDsfConfig)
        {
            executingConfigFile = true;
            _model.SetExecutingConfig(true);
        }

        // Flush this code channel to make sure it's our turn now
        if (!await _codeProcessor.FlushAsync(this))
        {
            using (await LockAsync())
            {
                Abort();
            }
        }

        // Start processing codes
        Queue<Code> codes = new();
        do
        {
            // Fill up the macro code buffer
            while (codes.Count < _settings.BufferedMacroCodes)
            {
                try
                {
                    Code? readCode = await ReadCodeAsync();
                    if (readCode is null)
                    {
                        // No more codes available
                        break;
                    }

                    codes.Enqueue(readCode);
                    await readCode.ExecuteAsync();       // actual execution happens in the background
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }

                        await _model.HandleMacroErrorAsync(FileName, LineNumber, e.Message);
                        await _dsfLogger.LogOutputAsync(MessageType.Error, $"in file {Path.GetFileName(FileName)} line {LineNumber}: {e.Message}");
                        _logger.Error(e);
                    }

                    using (await LockAsync())
                    {
                        Abort();
                    }
                }
            }

            // Wait for the next code to finish
            if (codes.TryDequeue(out Code? code))
            {
                try
                {
                    // Logging of regular messages is done by the code itself, no need to take care of it here
                    Message? codeResult = await code.Task;
                    if (codeResult?.Type is MessageType.Error)
                    {
                        await _model.HandleMacroErrorAsync(FileName, code.LineNumber ?? 0, codeResult.Content);
                    }
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }

                        await _model.HandleMacroErrorAsync(FileName, code.LineNumber ?? 0, e.Message);
                        await _dsfLogger.LogOutputAsync(MessageType.Error, $"in file {Path.GetFileName(FileName)} line {code.LineNumber ?? 0}: {e.Message}");
                        _logger.Warn(e);
                    }

                    using (await LockAsync())
                    {
                        Abort();
                    }
                }
            }
            else
            {
                // No more codes to process, macro file has finished
                _logger.Debug("{0}: Finished codes from macro file {1}", Channel, FileName);
                break;
            }
        }
        while (!_lifetime.ApplicationStopping.IsCancellationRequested);

        using (await LockAsync())
        {
            // No longer executing
            IsExecuting = false;
            if (!IsAborted)
            {
                if (Channel != CodeChannel.Daemon)
                {
                    _logger.Info("{0}: Finished macro file {1}", Channel, FileName);
                }
                else
                {
                    _logger.Debug("{0}: Finished macro file {1}", Channel, FileName);
                }
            }

            // Resolve potential tasks waiting for the macro result
            if (_finishTcs is not null)
            {
                _finishTcs.SetResult();
                _finishTcs = null;
            }

            // Check if we've finished executing a config file
            if (executingConfigFile)
            {
                _model.SetExecutingConfig(false);
            }

            // Release this instance when done
            Dispose();
        }
    }

    /// <summary>
    /// Indicates if this instance has been _disposed
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Dispose this instance
    /// </summary>
    public override void Dispose()
    {
        // Don't dispose this instance twice...
        if (_disposed)
        {
            return;
        }

        // Dispose used resources
        _cts.Dispose();
        base.Dispose();
        _finishTcs?.SetCanceled();
        _disposed = true;
    }
}
