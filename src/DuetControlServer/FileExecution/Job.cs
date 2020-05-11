using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.Files;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
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
    /// Lock this class whenver it is accessed (except for <see cref="Diagnostics(StringBuilder)"/>)
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
        private static readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Lock this class
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static IDisposable Lock() => _lock.Lock(Program.CancellationToken);

        /// <summary>
        /// Lock this class asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync(Program.CancellationToken);

        /// <summary>
        /// Condition to trigger when the print is supposed to resume
        /// </summary>
        private static readonly AsyncConditionVariable _resume = new AsyncConditionVariable(_lock);

        /// <summary>
        /// Condition to trigger when the print has finished
        /// </summary>
        private static readonly AsyncConditionVariable _finished = new AsyncConditionVariable(_lock);

        /// <summary>
        /// Job file being read from
        /// </summary>
        private static CodeFile _file;

        /// <summary>
        /// Indicates if a file has been selected for printing
        /// </summary>
        public static bool IsFileSelected { get => _file != null; }

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
        public static long FileLength { get => _file.Length; }

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
            ParsedFileInfo info = await InfoParser.Parse(fileName);
            CodeFile file = new CodeFile(fileName, CodeChannel.File);

            // A file being printed may start another file print
            if (IsFileSelected)
            {
                await Cancel();
                await _finished.WaitAsync(Program.CancellationToken);
            }

            // Update the state
            IsCancelled = IsAborted = false;
            IsSimulating = simulating;
            _file = file;
            _pausePosition = null;

            // Update the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Inputs.File.Volumetric = false;
                Model.Provider.Get.Job.File.Assign(info);
            }

            // Notify RepRapFirmware and start processing the file in the background
            _logger.Info("Selected file {0}", _file.FileName);
        }

        /// <summary>
        /// Perform actual print jobs
        /// </summary>
        public static async Task Run()
        {
            do
            {
                // Wait for the next print to start
                bool startingNewPrint;
                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    await _resume.WaitAsync(Program.CancellationToken);
                    startingNewPrint = !_file.IsClosed;
                    IsProcessing = startingNewPrint;
                }

                // Deal with the file print
                if (startingNewPrint)
                {
                    _logger.Info("Starting file print");

                    // Notify RRF
                    SPI.Interface.SetPrintStarted();

                    // Process the file
                    Queue<Code> codes = new Queue<Code>();
                    Queue<Task<CodeResult>> codeTasks = new Queue<Task<CodeResult>>();
                    long nextFilePosition = 0;
                    do
                    {
                        // Fill up the code buffer unless the print is paused
                        while (true)
                        {
                            using (await _lock.LockAsync(Program.CancellationToken))
                            {
                                if (IsPaused || codeTasks.Count >= Math.Max(1, Settings.BufferedPrintCodes))
                                {
                                    break;
                                }
                            }

                            try
                            {
                                Code readCode = await _file.ReadCodeAsync();
                                if (readCode == null)
                                {
                                    break;
                                }

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

                                await Utility.Logger.LogOutput(MessageType.Error, $"Failed to read code from job file: {ae.InnerException.Message}");
                                _logger.Error(ae.InnerException);
                            }
                            catch (Exception e)
                            {
                                using (await _lock.LockAsync(Program.CancellationToken))
                                {
                                    _file.Close();
                                }

                                await Utility.Logger.LogOutput(MessageType.Error, $"Failed to read code from job file: {e.Message}");
                                _logger.Error(e);
                            }
                        }

                        // Is there anything more to do?
                        if (codes.TryDequeue(out Code code))
                        {
                            try
                            {
                                CodeResult result = await codeTasks.Dequeue();
                                nextFilePosition = code.FilePosition.Value + code.Length.Value;
                                await Utility.Logger.LogOutput(result);
                            }
                            catch (OperationCanceledException)
                            {
                                // Code has been cancelled, don't log this. In the future this may terminate the job file
                                // Note this can happen as well when the file being printed is exchanged
                            }
                            catch (CodeParserException cpe)
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, cpe.Message);
                            }
                            catch (AggregateException ae)
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{ae.InnerException.GetType().Name}] {ae.InnerException.Message}");
                            }
                            catch (Exception e)
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{e.GetType().Name}] {e.Message}");
                            }
                        }
                        else
                        {
                            using (await LockAsync())
                            {
                                if (IsPaused)
                                {
                                    // Adjust the file position
                                    long newFilePosition = (_pausePosition != null) ? _pausePosition.Value : nextFilePosition;
                                    await SetFilePosition(newFilePosition);
                                    _logger.Info("Job has been paused at byte {0}, reason {1}", newFilePosition, _pauseReason);

                                    // Wait for the print to be resumed
                                    IsProcessing = false;
                                    await _resume.WaitAsync(Program.CancellationToken);
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

                    using (await _lock.LockAsync(Program.CancellationToken))
                    {
                        // Notify RepRapFirmware that the print file has been closed
                        if (IsCancelled)
                        {
                            _logger.Info("Cancelled job file");
                            await SPI.Interface.SetPrintStopped(PrintStoppedReason.UserCancelled);
                        }
                        else if (IsAborted)
                        {
                            _logger.Info("Aborted job file");
                            await SPI.Interface.SetPrintStopped(PrintStoppedReason.Abort);
                        }
                        else
                        {
                            _logger.Info("Finished job file");
                            await SPI.Interface.SetPrintStopped(PrintStoppedReason.NormalCompletion);
                        }

                        // Update the object model again
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.Job.LastFileAborted = IsAborted;
                            Model.Provider.Get.Job.LastFileCancelled = IsCancelled;
                            Model.Provider.Get.Job.LastFileSimulated = IsSimulating;
                            Model.Provider.Get.Job.LastFileName = Model.Provider.Get.Job.File.FileName;
                        }
                    }
                }

                using (await _lock.LockAsync(Program.CancellationToken))
                {
                    // We are no longer printing a file...
                    _finished.NotifyAll();

                    // Dispose the file
                    _file.Dispose();
                    _file = null;

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
                Code.CancelPending(CodeChannel.File);
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
        /// Cancel the current print (e.g. when M0 is called)
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Cancel()
        {
            if (IsFileSelected)
            {
                using (await _file.LockAsync())
                {
                    _file.Close();
                }
                IsCancelled = true;
                Resume();
            }
        }

        /// <summary>
        /// Abort the current print. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Abort()
        {
            if (IsFileSelected)
            {
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
