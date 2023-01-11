using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Class representing a macro being executed
    /// </summary>
    public sealed class Macro : IDisposable
    {
        /// <summary>
        /// Static logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Lock for this instance
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
        /// Channel on which this macro is running
        /// </summary>
        public CodeChannel Channel { get; }

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
        /// File to read from
        /// </summary>
        private readonly CodeFile? _file;

        /// <summary>
        /// Name of the file being executed
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Whether this file is config.g or config.g.bak
        /// </summary>
        public bool IsConfig { get; }

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
        private ConfigExtraSteps _extraStep = ConfigExtraSteps.SendHostname;

        /// <summary>
        /// Whether this file is config-override.g
        /// </summary>
        public bool IsConfigOverride { get; }

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
        /// Indicates if the macro file could be opened
        /// </summary>
        public bool FileOpened => _file is not null;

        /// <summary>
        /// Constructor of a macro
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="physicalFile">Physical path of the macro</param>
        /// <param name="channel">Code requesting the macro</param>
        /// <param name="isNested">Whether the code was started from a G/M/T-code</param>
        /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
        public Macro(string fileName, string physicalFile, CodeChannel channel, bool isNested = false, int sourceConnection = 0)
        {
            FileName = fileName;
            Channel = channel;
            IsNested = isNested;
            SourceConnection = sourceConnection;

            // Are we executing config.g?
            string name = Path.GetFileName(physicalFile);
            if (isNested)
            {
                IsConfigOverride = (name == FilePath.ConfigOverrideFile);
            }
            else if (name == FilePath.ConfigFile || name == FilePath.ConfigFileFallback)
            {
                IsConfig = true;
            }

            // Try to start the macro file
            try
            {
                _file = new CodeFile(physicalFile, channel);
                _logger.Info("Starting macro file {0} on channel {1}", fileName, channel);
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
        }

        /// <summary>
        /// Start the execution of this macro file
        /// </summary>
        public void Start()
        {
            string name = Path.GetFileName(FileName);
            if (_file is not null || (name == FilePath.ConfigFile && _file is not null) || name == FilePath.ConfigFileFallback)
            {
                IsExecuting = JustStarted = true;
                _ = Task.Run(Run);
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

            if (_file is not null)
            {
                using (_file.Lock())
                {
                    _file.Close();
                }
            }

            _logger.Info("Aborted macro file {0}", FileName);
        }

        /// <summary>
        /// Abort this macro asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task AbortAsync()
        {
            if (IsAborted || _disposed)
            {
                return;
            }
            IsAborted = true;
            _cts.Cancel();

            if (_file is not null)
            {
                using (await _file.LockAsync())
                {
                    _file.Close();
                }
            }

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
        /// Method representing the lifecycle of a macro being executed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Run()
        {
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
                    catch (OperationCanceledException oce)
                    {
                        using (await _lock.LockAsync(Program.CancellationToken))
                        {
                            if (!IsAborted)
                            {
                                _logger.Debug(oce, "Cancelling macro file because of cancelled code");
                                await AbortAsync();
                            }
                        }
                    }
                    catch (AggregateException ae)
                    {
                        using (await _file!.LockAsync())
                        {
                            _file.Close();
                        }

                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to read code from macro {Path.GetFileName(FileName)}: {ae.InnerException!.Message}");
                        _logger.Error(ae.InnerException, "Failed to read code from macro {0}", FileName);
                    }
                    catch (Exception e)
                    {
                        using (await _file!.LockAsync())
                        {
                            _file.Close();
                        }

                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to read code from macro {Path.GetFileName(FileName)}: {e.Message}");
                        _logger.Error(e, "Failed to read code from macro {0}", FileName);
                    }
                }

                // Wait for the next code to finish
                if (codes.TryDequeue(out Code? code))
                {
                    try
                    {
                        // Logging of regular messages is done by the code itself, no need to take care of it here
                        await code.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled, don't log this. This can happen when a pausable macro is interrupted
                    }
                    catch (CodeParserException cpe)
                    {
                        await Logger.LogOutputAsync(MessageType.Error, cpe.Message + " of " + Path.GetFileName(FileName));
                        _logger.Debug(cpe);
                    }
                    catch (AggregateException ae)
                    {
                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to execute {code.ToShortString()} in {Path.GetFileName(FileName)}: [{ae.InnerException!.GetType().Name}] {ae.InnerException.Message}");
                        _logger.Warn(ae);
                    }
                    catch (Exception e)
                    {
                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to execute {code.ToShortString()} in {Path.GetFileName(FileName)}: [{e.GetType().Name}] {e.Message}");
                        _logger.Warn(e);
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

            // Resolve potential tasks waiting for the macro result
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                IsExecuting = false;
                if (!IsAborted)
                {
                    _logger.Info("Finished macro file {0}", FileName);
                }
                if (_finishTcs is not null)
                {
                    _finishTcs.SetResult();
                    _finishTcs = null;
                }
            }

            // Release this instance when done
            Dispose();
        }

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
                switch (_extraStep)
                {
                    case ConfigExtraSteps.SendHostname:
                        result = new Code
                        {
                            Channel = Channel,
                            Flags = CodeFlags.IsInternallyProcessed,        // don't check our own hostname
                            Type = CodeType.MCode,
                            MajorNumber = 550
                        };
                        result.Parameters.Add(new CodeParameter('P', Environment.MachineName));
                        _extraStep = ConfigExtraSteps.SendDateTime;
                        break;

                    case ConfigExtraSteps.SendDateTime:
                        result = new Code
                        {
                            Channel = Channel,
                            Flags = CodeFlags.IsInternallyProcessed,        // don't update our own datetime
                            Type = CodeType.MCode,
                            MajorNumber = 905
                        };
                        result.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                        result.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                        _extraStep = ConfigExtraSteps.Done;
                        break;

                    default:
                        result = (_file is not null) ? await _file.ReadCodeAsync() : null;
                        break;
                }
            }
            else
            {
                result = (_file is not null) ? await _file.ReadCodeAsync() : null;
            }

            // Update code information
            if (result is not null)
            {
                result.CancellationToken = CancellationToken;
                result.FilePosition = null;
                result.Flags |= CodeFlags.Asynchronous | CodeFlags.IsFromMacro;
                result.Macro = this;
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
        /// Indicates if this instance has been _disposed
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            // Don't dispose this instance twice...
            if (_disposed)
            {
                return;
            }

            // Dispose the used resources
            _cts.Dispose();
            _file?.Dispose();
            _finishTcs?.SetCanceled();
            _disposed = true;
        }
    }
}
