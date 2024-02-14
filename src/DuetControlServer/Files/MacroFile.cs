using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Class representing a macro being executed
    /// </summary>
    public sealed class MacroFile : CodeFile, IDisposable
    {
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
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

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
        /// Create a macro file for execution on the given channel
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="physicalFile">Physical path of the macro</param>
        /// <param name="channel">Code requesting the macro</param>
        /// <param name="startingCode">Code starting the macro file</param>
        /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
        /// <returns>Macro file or null if it could not be opened</returns>

        public static MacroFile? Open(string fileName, string physicalFile, CodeChannel channel, Code? startCode = null, int sourceConnection = 0)
        {
            try
            {
                MacroFile macro = new(fileName, physicalFile, channel, startCode, sourceConnection);
                _logger.Info("Starting macro file {0} on channel {1}", fileName, channel);
                return macro;
            }
            catch (FileNotFoundException)
            {
                if (channel != CodeChannel.Daemon)
                {
                    _logger.Debug("Macro file {0} not found", fileName);
                }
                else
                {
                    _logger.Trace("Macro file {0} not found", fileName);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to start macro file {0}: {1}", fileName, e.Message);
            }
            return null;
        }

        /// <summary>
        /// Constructor of a macro
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="physicalFile">Physical path of the macro</param>
        /// <param name="channel">Code requesting the macro</param>
        /// <param name="startCode">Code starting the macro file</param>
        /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
        private MacroFile(string fileName, string physicalFile, CodeChannel channel, Code? startCode, int sourceConnection) : base(fileName, physicalFile, channel)
        {
            SourceConnection = sourceConnection;

            // Are we executing config.g, config-override.g, or dsf-config.g?
            if (startCode is not null)
            {
                IsNested = true;
                IsConfigOverride = startCode is { Type: CodeType.MCode, MajorNumber: 501 } && (fileName == FilePath.ConfigOverrideFile);
                IsDsfConfig = fileName == FilePath.DsfConfigFile;
            }
            else if (physicalFile == Path.Combine(Settings.BaseDirectory, "sys", FilePath.ConfigFile) ||
                     physicalFile == Path.Combine(Settings.BaseDirectory, "sys", FilePath.ConfigFileFallback))
            {
                IsConfig = true;
            }
        }

        /// <summary>
        /// Start executing this macro file in the background
        /// </summary>
        public void Start()
        {
            IsExecuting = JustStarted = true;
            Task.Run(Run);
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
                        result = new Code
                        {
                            Channel = Channel,
                            File = this,
                            Flags = CodeFlags.IsInternallyProcessed,        // don't check our own hostname
                            Type = CodeType.MCode,
                            MajorNumber = 550
                        };
                        result.Parameters.Add(new CodeParameter('P', Environment.MachineName));
                        _extraConfigStep = ConfigExtraSteps.SendDateTime;
                        break;

                    case ConfigExtraSteps.SendDateTime:
                        result = new Code
                        {
                            Channel = Channel,
                            File = this,
                            Flags = CodeFlags.IsInternallyProcessed,        // don't update our own datetime
                            Type = CodeType.MCode,
                            MajorNumber = 905
                        };
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
        private async Task Run()
        {
            // Reset start-up error
            if (IsConfig)
            {
                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.State.StartupError = null;
                }
            }

            // Check if we're executing a config file
            bool executingConfigFile = false;
            if (IsConfig || IsConfigOverride || IsDsfConfig)
            {
                executingConfigFile = true;
                Model.Provider.SetExecutingConfig(true);
            }

            // Start processing codes
            Queue<Code> codes = new();
            do
            {
                // Fill up the macro code buffer
                while (codes.Count < Settings.BufferedMacroCodes)
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
                        await readCode.Execute();       // actual execution happens in the background
                    }
                    catch (Exception e)
                    {
                        if (e is not OperationCanceledException)
                        {
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException!;
                            }

                            await Model.Provider.HandleMacroErrorAsync(FileName, LineNumber, e.Message);
                            await Utility.Logger.LogOutputAsync(MessageType.Error, $"in file {Path.GetFileName(FileName)} line {LineNumber}: {e.Message}");
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
                            await Model.Provider.HandleMacroErrorAsync(FileName, code.LineNumber ?? 0, codeResult.Content);
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

                            await Model.Provider.HandleMacroErrorAsync(FileName, code.LineNumber ?? 0, e.Message);
                            await Utility.Logger.LogOutputAsync(MessageType.Error, $"in file {Path.GetFileName(FileName)} line {code.LineNumber ?? 0}: {e.Message}");
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
                    _logger.Debug("Finished codes from macro file {0}", FileName);
                    break;
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);

            using (await LockAsync())
            {
                // No longer executing
                IsExecuting = false;
                if (!IsAborted)
                {
                    _logger.Info("Finished macro file {0}", FileName);
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
                    Model.Provider.SetExecutingConfig(false);
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
}
