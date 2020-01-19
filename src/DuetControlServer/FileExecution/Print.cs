using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Main class dealing with file prints.
    /// Lock this class whenver it is accessed (except for Diagnostics)
    /// </summary>
    public static class Print
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
        /// Lock this class asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync();

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
        private static BaseFile _file;

        /// <summary>
        /// Indicates if a file has been selected for printing
        /// </summary>
        public static bool IsFileSelected { get => _file != null; }

        /// <summary>
        /// Indicates if a print is live
        /// </summary>
        public static bool IsPrinting { get; private set; }

        /// <summary>
        /// Indicates if a file is being simulated
        /// </summary>
        public static bool IsSimulating { get; private set; }

        /// <summary>
        /// Indicates if the file print has been paused
        /// </summary>
        public static bool IsPaused { get; private set; }

        /// <summary>
        /// Indicates if the file print has been aborted
        /// </summary>
        public static bool IsAborted { get; private set; }

        /// <summary>
        /// Defines if the file position is supposed to be set by the Print task
        /// </summary>
        private static bool _pausePositionSet;

        /// <summary>
        /// Reason why the print has been paused
        /// </summary>
        private static PrintPausedReason _pauseReason;

        /// <summary>
        /// Reports the current file position
        /// </summary>
        public static long FilePosition
        {
            get => _file.Position;
            set
            {
                _file.Position = value;
                _pausePositionSet = true;
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
        public static async Task SelectFile(string fileName, bool simulating = false)
        {
            // Analyze and open the file
            ParsedFileInfo info = await FileInfoParser.Parse(fileName);
            BaseFile file = new BaseFile(fileName, CodeChannel.File);

            // A file being printed may start another file print
            if (IsFileSelected)
            {
                Cancel();
                await _finished.WaitAsync(Program.CancelSource.Token);
            }

            // Update the state
            IsPaused = IsAborted = false;
            IsSimulating = simulating;
            _file = file;

            // Update the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Channels[CodeChannel.File].VolumetricExtrusion = false;
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
                using (await _lock.LockAsync())
                {
                    await _resume.WaitAsync(Program.CancelSource.Token);
                    startingNewPrint = !_file.IsAborted;
                    IsPrinting = startingNewPrint;
                    _pausePositionSet = false;
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
                        using (await _lock.LockAsync())
                        {
                            while (!IsPaused && codeTasks.Count < Math.Max(2, Settings.BufferedPrintCodes))
                            {
                                Code readCode = _file.ReadCode();
                                if (readCode == null)
                                {
                                    // Cannot read any more codes
                                    break;
                                }

                                codes.Enqueue(readCode);
                                codeTasks.Enqueue(readCode.Execute());
                            }
                        }

                        // Is there anything more to do?
                        if (codes.TryDequeue(out Code code))
                        {
                            long lastFilePosition = nextFilePosition;
                            try
                            {
                                nextFilePosition = code.FilePosition.Value + code.Length.Value;
                                CodeResult result = await codeTasks.Dequeue();
                                await Utility.Logger.LogOutput(result);
                            }
                            catch (OperationCanceledException)
                            {
                                // May happen when the file being printed is exchanged, printed, or when a third-party plugin decided to cancel the code
                                nextFilePosition = lastFilePosition;
                            }
                            catch (Exception e)
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{e.GetType().Name}] {e.Message}");
                                _logger.Error(e);

                                using (await _lock.LockAsync())
                                {
                                    Abort();
                                    break;
                                }
                            }
                        }
                        else
                        {
                            using (await LockAsync())
                            {
                                if (IsPaused)
                                {
                                    // Check if the file position has to be adjusted
                                    if (!_pausePositionSet)
                                    {
                                        FilePosition = nextFilePosition;
                                        _logger.Info("Print has been paused at byte {0}*, reason {1}", nextFilePosition, _pauseReason);
                                    }

                                    // Wait for the print to be resumed
                                    IsPrinting = false;
                                    await _resume.WaitAsync(Program.CancelSource.Token);
                                    _pausePositionSet = false;
                                }
                                else
                                {
                                    // No more codes available - print must have finished
                                    break;
                                }
                            }
                        }
                    }
                    while (!Program.CancelSource.IsCancellationRequested);

                    using (await _lock.LockAsync())
                    {
                        // Notify RepRapFirmware that the print file has been closed
                        if (_file.IsAborted)
                        {
                            if (IsAborted)
                            {
                                _logger.Info("Aborted print file");
                                await SPI.Interface.SetPrintStopped(PrintStoppedReason.Abort);
                            }
                            else
                            {
                                _logger.Info("Cancelled print file");
                                await SPI.Interface.SetPrintStopped(PrintStoppedReason.UserCancelled);
                            }
                        }
                        else
                        {
                            _logger.Info("Finished print file");
                            await SPI.Interface.SetPrintStopped(PrintStoppedReason.NormalCompletion);
                        }

                        // Update the object model again
                        using (await Model.Provider.AccessReadWriteAsync())
                        {
                            Model.Provider.Get.Job.LastFileAborted = _file.IsAborted && IsAborted;
                            Model.Provider.Get.Job.LastFileCancelled = _file.IsAborted && !IsAborted;
                            Model.Provider.Get.Job.LastFileName = Model.Provider.Get.Job.File.FileName;
                        }
                    }
                }

                using (await _lock.LockAsync())
                {
                    // Dispose the file
                    _file.Dispose();
                    _file = null;

                    // End
                    IsPrinting = false;
                    _finished.NotifyAll();
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        /// <summary>
        /// Called when the print is being paused
        /// </summary>
        /// <param name="filePosition">File position where the print was paused</param>
        /// <param name="pauseReason">Reason why the print has been paused</param>
        public static void Pause(long? filePosition, PrintPausedReason pauseReason)
        {
            Code.CancelPending(CodeChannel.File);
            IsPaused = true;

            if (filePosition == null)
            {
                _pausePositionSet = false;
                _pauseReason = pauseReason;
            }
            else
            {
                FilePosition = filePosition.Value;
                _logger.Info("Print has been paused at byte {0}, reason {1}", filePosition, pauseReason);
            }
        }

        /// <summary>
        /// Resume a file print
        /// </summary>
        public static void Resume()
        {
            if (IsFileSelected && !IsPrinting)
            {
                IsPaused = false;
                _resume.NotifyAll();
            }
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0 is called)
        /// </summary>
        public static void Cancel()
        {
            Code.CancelPending(CodeChannel.File);
            _file?.Abort();
            Resume();
        }

        /// <summary>
        /// Abort the current print. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Abort()
        {
            IsAborted = true;
            Cancel();
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            using (await _lock.LockAsync())
            {
                if (IsFileSelected)
                {
                    builder.Append($"File {_file.FileName} is selected");
                    if (IsPrinting)
                    {
                        builder.Append(", printing");
                    }
                    if (IsSimulating)
                    {
                        builder.Append(", simulating");
                    }
                    if (IsPaused)
                    {
                        builder.Append(", paused");
                    }
                    if (IsAborted)
                    {
                        builder.Append(", aborted");
                    }
                    builder.AppendLine();
                }
            }
        }
    }
}
