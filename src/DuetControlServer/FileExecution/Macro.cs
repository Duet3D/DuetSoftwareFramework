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
        /// Internal cancellation token source used for codes
        /// </summary>
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

        /// <summary>
        /// Cancellation token that is triggered when the file is cancelled/aborted
        /// </summary>
        public CancellationToken CancellationToken { get => _cts.Token; }

        /// <summary>
        /// Internal lock used for starting codes in the right order
        /// </summary>
        private readonly AsyncLock _codeStartLock = new();

        /// <summary>
        /// Internal lock used for starting codes in the right order
        /// </summary>
        private readonly AsyncLock _codeFinishLock = new();

        /// <summary>
        /// Method to wait until a new code can be started in the right order
        /// </summary>
        /// <returns>Disposable lock</returns>
        /// <remarks>
        /// This is required in case a flush is requested before another nested macro is started
        /// </remarks>
        public AwaitableDisposable<IDisposable> WaitForCodeStart() => _codeStartLock.LockAsync(CancellationToken);

        /// <summary>
        /// Method to wait until a new code can be finished in the right order
        /// </summary>
        /// <returns>Disposable lock</returns>
        /// <remarks>
        /// This is required in case a flush is requested before another nested macro is started
        /// </remarks>
        public AwaitableDisposable<IDisposable> WaitForCodeFinish() => _codeFinishLock.LockAsync(CancellationToken);

        /// <summary>
        /// File to read from
        /// </summary>
        private readonly CodeFile _file;

        /// <summary>
        /// Name of the file being executed
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Indicates if config.g is being processed
        /// </summary>
        public static bool RunningConfig { get => _runningConfig; }
        private static volatile bool _runningConfig;

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
        public bool FileOpened { get => _file != null; }

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
                _runningConfig = true;
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
            finally
            {
                if (_file != null || (name == FilePath.ConfigFile && _file != null) || name == FilePath.ConfigFileFallback)
                {
                    IsExecuting = JustStarted = true;
                    _ = Task.Run(Run);
                }
            }
        }

        /// <summary>
        /// Abort this macro
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task Abort()
        {
            if (IsAborted || disposed)
            {
                return;
            }
            IsAborted = true;
            _cts.Cancel();

            if (_file != null)
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
        private TaskCompletionSource _finishTCS;

        /// <summary>
        /// Wait for this macro to finish asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// This task is always resolved and never cancelled
        /// </remarks>
        public Task FinishAsync()
        {
            if (!IsExecuting)
            {
                return Task.CompletedTask;
            }

            if (_finishTCS != null)
            {
                return _finishTCS.Task;
            }
            _finishTCS = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _finishTCS.Task;
        }

        /// <summary>
        /// Method representing the lifecycle of a macro being executed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Run()
        {
            Queue<Code> codes = new();
            Queue<Task<Message>> codesBeingExecuted = new();

            do
            {
                // Fill up the macro code buffer
                while (codes.Count < Settings.BufferedMacroCodes)
                {
                    try
                    {
                        Code readCode = await ReadCodeAsync();
                        if (readCode == null)
                        {
                            // No more codes available
                            break;
                        }

                        codes.Enqueue(readCode);
                        codesBeingExecuted.Enqueue(readCode.Execute());
                    }
                    catch (OperationCanceledException)
                    {
                        using (await _lock.LockAsync(Program.CancellationToken))
                        {
                            await Abort();
                        }
                    }
                    catch (AggregateException ae)
                    {
                        using (await _file.LockAsync())
                        {
                            _file.Close();
                        }

                        await Logger.LogOutput(MessageType.Error, $"Failed to read code from macro {Path.GetFileName(FileName)}: {ae.InnerException.Message}");
                        _logger.Error(ae.InnerException, "Failed to read code from macro {0}", FileName);
                    }
                    catch (Exception e)
                    {
                        using (await _file.LockAsync())
                        {
                            _file.Close();
                        }

                        await Logger.LogOutput(MessageType.Error, $"Failed to read code from macro {Path.GetFileName(FileName)}: {e.Message}");
                        _logger.Error(e, "Failed to read code from macro {0}", FileName);
                    }
                }

                // Wait for the next code to finish
                if (codes.TryDequeue(out Code code) && codesBeingExecuted.TryDequeue(out Task<Message> codeTask))
                {
                    try
                    {
                        Message result = await codeTask;
                        await Model.Provider.Output(result);
                    }
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled, don't log this. In the future this may terminate the macro file
                    }
                    catch (CodeParserException cpe)
                    {
                        await Logger.LogOutput(MessageType.Error, cpe.Message + " of " + Path.GetFileName(FileName));
                        _logger.Debug(cpe);
                    }
                    catch (AggregateException ae)
                    {
                        await Logger.LogOutput(MessageType.Error, $"Failed to execute {code.ToShortString()} in {Path.GetFileName(FileName)}: [{ae.InnerException.GetType().Name}] {ae.InnerException.Message}");
                        _logger.Warn(ae);
                    }
                    catch (Exception e)
                    {
                        await Logger.LogOutput(MessageType.Error, $"Failed to execute {code.ToShortString()} in {Path.GetFileName(FileName)}: [{e.GetType().Name}] {e.Message}");
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
                if (_finishTCS != null)
                {
                    _finishTCS.SetResult();
                    _finishTCS = null;
                }
            }

            // Release this instance when done
            Dispose();
        }

        /// <summary>
        /// Read the next available code asynchronously
        /// </summary>
        /// <returns>Read code</returns>
        private async Task<Code> ReadCodeAsync()
        {
            Code result;

            // When executing config.g, perform some extra steps...
            if (IsConfig)
            {
                switch (_extraStep)
                {
                    case ConfigExtraSteps.SendHostname:
                        result = new Code
                        {
                            Channel = Channel,
                            InternallyProcessed = true,          // don't check our own hostname
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
                            InternallyProcessed = true,          // don't update our own datetime
                            Type = CodeType.MCode,
                            MajorNumber = 905
                        };
                        result.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                        result.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                        _extraStep = ConfigExtraSteps.Done;
                        break;

                    default:
                        result = (_file != null) ? await _file.ReadCodeAsync() : null;
                        break;
                }
            }
            else
            {
                result = (_file != null) ? await _file.ReadCodeAsync() : null;
            }

            // Update code information
            if (result != null)
            {
                result.CancellationToken = CancellationToken;
                result.FilePosition = null;
                result.Flags |= CodeFlags.IsFromMacro;
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
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed = false;

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            // Don't dispose this instance twice...
            if (disposed)
            {
                return;
            }

            // Synchronize the machine model one last time while config.g or config-override.g is being run.
            // This makes sure the filaments can be properly assigned
            if (IsConfig || IsConfigOverride)
            {
                _ = Task.Run(async () =>
                {
                    await Model.Updater.WaitForFullUpdate();
                    FilamentManager.RefreshMapping();
                    _runningConfig = false;
                });
            }

            // Dispose the used resources
            _cts.Dispose();
            _file?.Dispose();
            _finishTCS?.SetCanceled();
            disposed = true;
        }
    }
}
