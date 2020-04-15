using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.Files;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly AsyncLock _lock = new AsyncLock();

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
        /// List of messages written by the codes
        /// </summary>
        public CodeResult Result { get; } = new CodeResult();

        /// <summary>
        /// Internal lock used for starting codes in the right order
        /// </summary>
        private readonly AsyncLock _codeStartLock = new AsyncLock();

        /// <summary>
        /// Method to wait until a new code can be started in the right order
        /// </summary>
        /// <returns>Disposable lock</returns>
        /// <remarks>
        /// This is required in case a flush is requested before another nested macro is started
        /// </remarks>
        public AwaitableDisposable<IDisposable> WaitForCodeExecution() => _codeStartLock.LockAsync(_file.CancellationToken);

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
        /// Indicates if the macro file is being executed
        /// </summary>
        public bool IsExecuting { get; private set; }

        /// <summary>
        /// Indicates if the macro file has been aborted
        /// </summary>
        public bool IsAborted { get; private set; }

        /// <summary>
        /// Indicates if an error occurred while executing the macro file
        /// </summary>
        public bool HadError { get; private set; }

        /// <summary>
        /// Constructor of a macro
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="channel">Code requesting the macro</param>
        /// <param name="isNested">Whether the code was started from a G/M/T-code</param>
        /// <param name="sourceConnection">Original IPC connection requesting this macro file</param>
        public Macro(string fileName, CodeChannel channel, bool isNested, int sourceConnection)
        {
            FileName = fileName;
            Channel = channel;
            IsNested = isNested;
            SourceConnection = sourceConnection;

            // Are we executing config.g?
            string name = Path.GetFileName(fileName);
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
                _file = new CodeFile(fileName, channel);
                _logger.Info("Starting macro file {0} on channel {1}", name, channel);
            }
            catch (Exception e)
            {
                if (!(e is FileNotFoundException))
                {
                    _logger.Error(e, "Failed to start macro file {0}: {1}", name, e.Message);
                }
                HadError = true;
            }
            finally
            {
                if (IsConfig || _file != null)
                {
                    IsExecuting = true;
                    _ = Task.Run(Run);
                }
            }
        }

        /// <summary>
        /// Abort this macro
        /// </summary>
        public void Abort()
        {
            if (IsAborted)
            {
                return;
            }
            IsAborted = HadError = true;
            IsExecuting = false;
            _file?.Abort();
            
            if (FileName != null)
            {
                _logger.Info("Aborted macro file {0}", Path.GetFileName(FileName));
            }
            else
            {
                _logger.Info("Aborted invalid macro file");
            }
        }

        /// <summary>
        /// Method representing the lifecycle of a macro being executed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Run()
        {
            Queue<Code> codes = new Queue<Code>();
            Queue<Task<CodeResult>> codesBeingExecuted = new Queue<Task<CodeResult>>();

            do
            {
                // Fill up the macro code buffer
                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    while (codes.Count < Settings.BufferedMacroCodes)
                    {
                        Code readCode = await ReadCode();
                        if (readCode == null)
                        {
                            // No more codes available
                            break;
                        }

                        codes.Enqueue(readCode);
                        codesBeingExecuted.Enqueue(readCode.Execute());
                    }
                }

                // Wait for the next code to finish
                if (codes.TryDequeue(out Code code) && codesBeingExecuted.TryDequeue(out Task<CodeResult> codeTask))
                {
                    try
                    {
                        CodeResult result = await codeTask;
                        if (!result.IsEmpty)
                        {
                            Result.AddRange(result);
                            if (!IsNested)
                            {
                                await Utility.Logger.LogOutput(result);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled. Don't log this
                    }
                    catch (AggregateException ae)
                    {
                        await Utility.Logger.LogOutput(MessageType.Error, $"Failed to execute {code.ToShortString()}: [{ae.InnerException.GetType().Name}] {ae.InnerException.Message}");
                        using (await _lock.LockAsync(Program.CancellationToken))
                        {
                            Abort();
                        }
                    }
                    catch (Exception e)
                    {
                        await Utility.Logger.LogOutput(MessageType.Error, $"Failed to execute {code.ToShortString()}: [{e.GetType().Name}] {e.Message}");
                        using (await _lock.LockAsync(Program.CancellationToken))
                        {
                            Abort();
                        }
                    }
                }
                else
                {
                    using (await _lock.LockAsync(Program.CancellationToken))
                    {
                        // No more codes to process, macro file has finished
                        _logger.Debug("Finished codes from macro file {0}", Path.GetFileName(FileName));
                        IsExecuting = false;
                    }
                    break;
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Read the next available code
        /// </summary>
        /// <returns>Read code</returns>
        private async Task<Code> ReadCode()
        {
            Code result;

            try
            {
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
                    result.FilePosition = null;
                    result.Flags |= CodeFlags.IsFromMacro;
                    if (IsConfig) { result.Flags |= CodeFlags.IsFromConfig; }
                    if (IsConfigOverride) { result.Flags |= CodeFlags.IsFromConfigOverride; }
                    if (IsNested) { result.Flags |= CodeFlags.IsNestedMacro; }
                    result.Macro = this;
                    result.SourceConnection = SourceConnection;
                    return result;
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to read code from macro file {0}", Path.GetFileName(FileName));
                Abort();
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
                    await Model.Updater.WaitForFullUpdate(Program.CancellationToken);
                    _runningConfig = false;
                });
            }

            // Dispose the used resources
            _file?.Dispose();
            disposed = true;
        }
    }
}
