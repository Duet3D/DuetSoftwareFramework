using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Model;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Main class dealing with a file job
    /// </summary>
    /// <remarks>
    /// Lock this class whenever it is accessed (except for <see cref="Diagnostics(StringBuilder)"/>)
    /// </remarks>
    public static class Job
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Lock around the print class
        /// </summary>
        private static readonly AsyncLock _lock = new();

        /// <summary>
        /// Lock this class
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static IDisposable Lock() => _lock.Lock(Program.CancellationToken);

        /// <summary>
        /// Lock this class asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static Task<IDisposable> LockAsync() => _lock.LockAsync(Program.CancellationToken);

        /// <summary>
        /// Condition to trigger when the print is supposed to resume
        /// </summary>
        private static readonly AsyncConditionVariable _resume = new(_lock);

        /// <summary>
        /// Condition to trigger when the print has finished
        /// </summary>
        private static readonly AsyncConditionVariable _finished = new(_lock);

        /// <summary>
        /// Name of the job file
        /// </summary>
        private static string _filename;

        /// <summary>
        /// Job file being read from
        /// </summary>
        private static CodeFile _file;

        /// <summary>
        /// Internal cancellation token source used to cancel pending codes when necessary
        /// </summary>
        private static CancellationTokenSource _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

        /// <summary>
        /// Indicates if a file has been selected for printing
        /// </summary>
        public static bool IsFileSelected => _file != null;

        /// <summary>
        /// Indicates if a print is live
        /// </summary>
        public static bool IsProcessing { get; private set; }

        /// <summary>
        /// Indicates if a file is being simulated
        /// </summary>
        /// <remarks>
        /// This is volatile to allow fast access without locking the class first
        /// </remarks>
        public static bool IsSimulating
        {
            get => _isSimulating;
            private set => _isSimulating = value;
        }
        private static volatile bool _isSimulating;

        /// <summary>
        /// Indicates if the file print has been paused
        /// </summary>
        public static bool IsPaused { get; private set; }

        /// <summary>
        /// Indicates if the file print has been cancelled
        /// </summary>
        public static bool IsCancelled { get; private set; }

        /// <summary>
        /// Indicates if the file print has been aborted
        /// </summary>
        public static bool IsAborted { get; private set; }

        /// <summary>
        /// Defines if the file position is supposed to be set by the Print task
        /// </summary>
        private static long? _pausePosition;

        /// <summary>
        /// Reason why the print has been paused
        /// </summary>
        private static PrintPausedReason _pauseReason;

        /// <summary>
        /// Get the current file position
        /// </summary>
        /// <returns>File position</returns>
        public static async Task<long> GetFilePosition()
        {
            if (_file == null)
            {
                return 0;
            }
            using (await _file.LockAsync())
            {
                return _file.Position;
            }
        }

        /// <summary>
        /// Set the current file position
        /// </summary>
        /// <param name="filePosition">New file position</param>
        /// <returns>File position</returns>
        public static async Task SetFilePosition(long filePosition)
        {
            if (_file != null)
            {
                using (await _file.LockAsync())
                {
                    _file.Position = filePosition;
                }
            }
        }

        /// <summary>
        /// Returns the length of the file being printed in bytes
        /// </summary>
        public static long FileLength => _file.Length;

        /// <summary>
        /// Start a new file print
        /// </summary>
        /// <param name="fileName">File to print</param>
        /// <param name="simulating">Whether the file is being simulated</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// This class has to be locked when this method is called
        /// </remarks>
        public static async Task SelectFile(string fileName, bool simulating = false)
        {
            // Analyze and open the file
            GCodeFileInfo info = await InfoParser.Parse(fileName, true);
            CodeFile file = new(fileName, CodeChannel.File);

            // A file being printed may start another file print
            if (IsFileSelected)
            {
                await CancelAsync();
                await _finished.WaitAsync(Program.CancellationToken);
            }

            // Update the state
            IsCancelled = IsAborted = false;
            IsSimulating = simulating;
            _filename = fileName;
            _file = file;
            _pausePosition = null;

            // Update the object model
            using (await Provider.AccessReadWriteAsync())
            {
                Provider.Get.Inputs.File.Volumetric = false;
                Provider.Get.Job.File.Assign(info);
            }

            // Notify RepRapFirmware and start processing the file in the background
            await SPI.Interface.SetPrintFileInfo();
            _logger.Info("Selected file {0}", _file.FileName);
        }

        /// <summary>
        /// Perform actual print jobs
        /// </summary>
        public static async Task Run()
        {
            // Use a code pool for print files. This is possible for regular codes but should be avoided
            // for macro codes, because those codes may be referenced even after they finish
            Queue<Code> codePool = new();
            for (int i = 0; i < Math.Max(Settings.BufferedPrintCodes, 1); i++)
            {
                codePool.Enqueue(new Code());
            }

            do
            {
                // Wait for the next print to start
                CancellationToken cancellationToken;
                bool startingNewPrint;
                using (await LockAsync())
                {
                    await _resume.WaitAsync(Program.CancellationToken);
                    startingNewPrint = !_file.IsClosed;
                    cancellationToken = _cancellationTokenSource.Token;
                    IsProcessing = startingNewPrint;
                }

                // Deal with the file print
                if (startingNewPrint)
                {
                    _logger.Info("Starting file print");

                    // Process the file
                    Queue<Code> codes = new();
                    Queue<Task<Message>> codeTasks = new();
                    long nextFilePosition = 0;
                    do
                    {
                        // Fill up the code buffer
                        while (codePool.TryDequeue(out Code sharedCode))
                        {
                            sharedCode.Reset();

                            // Stop reading codes if the print has been paused or aborted
                            using (await LockAsync())
                            {
                                if (IsPaused)
                                {
                                    cancellationToken = _cancellationTokenSource.Token;
                                    codePool.Enqueue(sharedCode);
                                    break;
                                }
                            }

                            // Try to read the next code
                            try
                            {
                                Code readCode;
                                try
                                {
                                    readCode = await _file.ReadCodeAsync(sharedCode);
                                    if (readCode == null)
                                    {
                                        codePool.Enqueue(sharedCode);
                                        break;
                                    }
                                    readCode.CancellationToken = cancellationToken;
                                }
                                catch
                                {
                                    codePool.Enqueue(sharedCode);
                                    throw;
                                }

                                readCode.LogOutput = true;
                                codes.Enqueue(readCode);
                                codeTasks.Enqueue(readCode.Execute());
                            }
                            catch (OperationCanceledException)
                            {
                                using (await _file.LockAsync())
                                {
                                    _file.Close();
                                }
                            }
                            catch (AggregateException ae)
                            {
                                using (await _file.LockAsync())
                                {
                                    _file.Close();
                                }

                                await Logger.LogOutputAsync(MessageType.Error, $"Failed to read code from job file: {ae.InnerException.Message}");
                                _logger.Error(ae.InnerException);
                            }
                            catch (Exception e)
                            {
                                using (await LockAsync())
                                {
                                    _file.Close();
                                }

                                await Logger.LogOutputAsync(MessageType.Error, $"Failed to read code from job file: {e.Message}");
                                _logger.Error(e);
                            }
                        }

                        // Is there anything more to do?
                        if (codes.TryDequeue(out Code code) && codeTasks.TryDequeue(out Task<Message> codeTask))
                        {
                            try
                            {
                                try
                                {
                                    _ = await codeTask;     // Logging is done before we get here...
                                    nextFilePosition = code.FilePosition.Value + code.Length.Value;
                                }
                                catch (OperationCanceledException)
                                {
                                    // Code has been cancelled, don't log this.
                                    // Note this can happen as well when the file being printed is exchanged or when a pausable macro is interrupted
                                }
                                catch (CodeParserException cpe)
                                {
                                    await Logger.LogOutputAsync(MessageType.Error, cpe.Message);
                                }
                                catch (AggregateException ae)
                                {
                                    await Logger.LogOutputAsync(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{ae.InnerException.GetType().Name}] {ae.InnerException.Message}");
                                    _logger.Error(ae.InnerException);
                                }
                                catch (Exception e)
                                {
                                    await Logger.LogOutputAsync(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{e.GetType().Name}] {e.Message}");
                                    _logger.Error(e);
                                }
                            }
                            finally
                            {
                                codePool.Enqueue(code);
                            }
                        }
                        else
                        {
                            try
                            {
                                // Wait for custom codes to go in case a plugin inserted them
                                await SPI.Interface.Flush(CodeChannel.File);
                            }
                            catch (OperationCanceledException)
                            {
                                // ignored
                            }

                            using (await LockAsync())
                            {
                                if (IsPaused)
                                {
                                    // Adjust the file position
                                    long newFilePosition = _pausePosition ?? nextFilePosition;
                                    await SetFilePosition(newFilePosition);
                                    _logger.Info("Job has been paused at byte {0}, reason {1}", newFilePosition, _pauseReason);

                                    // Wait for the print to be resumed
                                    IsProcessing = false;
                                    await _resume.WaitAsync(Program.CancellationToken);
                                    IsProcessing = !IsAborted && !IsCancelled;
                                }
                                else
                                {
                                    // No more codes available - print must have finished
                                    break;
                                }
                            }
                        }
                    }
                    while (!Program.CancellationToken.IsCancellationRequested);

                    using (await LockAsync())
                    {
                        // Notify RepRapFirmware that the print file has been closed
                        if (IsCancelled)
                        {
                            // Prints are cancelled by M0/M1 which is processed by RRF
                            _logger.Info("Cancelled job file");
                        }
                        else if (IsAborted)
                        {
                            await SPI.Interface.StopPrint(PrintStoppedReason.Abort);
                            _logger.Info("Aborted job file");
                        }
                        else
                        {
                            await SPI.Interface.StopPrint(PrintStoppedReason.NormalCompletion);
                            _logger.Info("Finished job file");
                        }

                        // Update special fields that are not available in RRF
                        using (await Provider.AccessReadWriteAsync())
                        {
                            Provider.Get.Job.LastFileAborted = IsAborted;
                            Provider.Get.Job.LastFileCancelled = IsCancelled;
                            Provider.Get.Job.LastFileSimulated = IsSimulating;
                        }

                        // Update the last simulated time
                        if (IsSimulating && !IsAborted && !IsCancelled)
                        {
                            // Wait for the simulation time to be available
                            int? lastDuration = null;
                            int upTime = 0;
                            while (!Program.CancellationToken.IsCancellationRequested)
                            {
                                await Updater.WaitForFullUpdate();
                                using (await Provider.AccessReadOnlyAsync())
                                {
                                    if (Provider.Get.State.UpTime < upTime || Provider.Get.Job.LastDuration != null)
                                    {
                                        lastDuration = Provider.Get.Job.LastDuration;
                                        break;
                                    }
                                    upTime = Provider.Get.State.UpTime;
                                }
                            }

                            // Try to update the last simulated time
                            if (lastDuration > 0)
                            {
                                await InfoParser.UpdateSimulatedTime(_filename, lastDuration.Value);
                            }
                            else
                            {
                                _logger.Warn("Failed to update simulation time because it was not set in the object model");
                            }
                        }
                    }
                }

                using (await LockAsync())
                {
                    // We are no longer printing a file...
                    _finished.NotifyAll();

                    // Dispose the file
                    _file.Dispose();
                    _file = null;
                    _filename = null;

                    // End
                    IsProcessing = IsSimulating = IsPaused = false;
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Called when the print is being paused
        /// </summary>
        /// <param name="filePosition">File position where the print was paused</param>
        /// <param name="pauseReason">Reason why the print has been paused</param>
        public static void Pause(long? filePosition, PrintPausedReason pauseReason)
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                IsPaused = true;
                _pausePosition = filePosition;
                _pauseReason = pauseReason;
            }
        }

        /// <summary>
        /// Resume a file print
        /// </summary>
        public static void Resume()
        {
            if (IsFileSelected && !IsProcessing)
            {
                IsPaused = false;
                _resume.NotifyAll();
            }
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0/M1 is called)
        /// </summary>
        public static void Cancel()
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                using (_file.Lock())
                {
                    _file.Close();
                }
                IsCancelled = IsPaused;
                Resume();
            }
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0/M1 is called)
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task CancelAsync()
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                using (await _file.LockAsync())
                {
                    _file.Close();
                }
                IsCancelled = IsPaused;
                Resume();
            }
        }

        /// <summary>
        /// Abort the current print asynchronously. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Abort()
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                using (_file.Lock())
                {
                    _file.Close();
                }
                IsAborted = true;
                Resume();
            }
        }

        /// <summary>
        /// Abort the current print asynchronously. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task AbortAsync()
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                using (await _file.LockAsync())
                {
                    _file.Close();
                }
                IsAborted = true;
                Resume();
            }
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
            IDisposable lockObject = null;
            try
            {
                cts.CancelAfter(2000);
                lockObject = await _lock.LockAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                builder.AppendLine("Failed to lock Job task within 2 seconds");
            }

            if (IsFileSelected)
            {
                builder.Append($"File {_file.FileName} is selected");
                if (IsProcessing)
                {
                    builder.Append(", processing");
                }
                if (IsSimulating)
                {
                    builder.Append(", simulating");
                }
                if (IsPaused)
                {
                    builder.Append(", paused");
                }
                if (IsCancelled)
                {
                    builder.Append(", cancelled");
                }
                if (IsAborted)
                {
                    builder.Append(", aborted");
                }
                builder.AppendLine();
            }

            lockObject?.Dispose();
        }
    }
}
