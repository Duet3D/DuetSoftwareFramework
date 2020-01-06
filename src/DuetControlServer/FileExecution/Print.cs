using DuetAPI;
using DuetAPI.Commands;
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
        /// Reports the current file position
        /// </summary>
        public static long FilePosition
        {
            get => _file.Position;
            set => _file.Position = value;
        }

        /// <summary>
        /// Holds the file position after the current code being executed
        /// </summary>
        public static long? NextFilePosition { get; private set; }

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
            if (_file != null && IsPrinting)
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
                    do
                    {
                        using (await _lock.LockAsync())
                        {
                            // Check if the print has been paued
                            if (IsPaused)
                            {
                                IsPrinting = false;
                                await _resume.WaitAsync(Program.CancelSource.Token);
                            }

                            // Fill up the code buffer
                            while (codeTasks.Count == 0 || codeTasks.Count < Settings.BufferedPrintCodes)
                            {
                                Code readCode = _file.ReadCode();
                                if (readCode == null)
                                {
                                    // Print complete
                                    break;
                                }

                                codes.Enqueue(readCode);
                                codeTasks.Enqueue(readCode.Execute());
                            }
                        }

                        // Is there anything more to do?
                        if (codes.TryDequeue(out Code code))
                        {
                            try
                            {
                                if (codes.TryPeek(out Code nextCode) && nextCode.FilePosition != null)
                                {
                                    using (await _lock.LockAsync())
                                    {
                                        // Keep track of the next code's file position in case a macro is being executed
                                        NextFilePosition = nextCode.FilePosition;
                                    }
                                }

                                CodeResult result = await codeTasks.Dequeue();
                                await Utility.Logger.LogOutput(result);
                            }
                            catch (OperationCanceledException)
                            {
                                // May happen when the file being printed is exchanged or
                                // when a third-party application decides to cancel a code
                            }
                            catch (Exception e)
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{e.GetType().Name}] {e.Message}");
                                using (await _lock.LockAsync())
                                {
                                    Abort();
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // No more codes available - print must have finished
                            break;
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
                                await SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.Abort);
                            }
                            else
                            {
                                _logger.Info("Cancelled print file");
                                await SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.UserCancelled);
                            }
                        }
                        else
                        {
                            _logger.Info("Finished print file");
                            await SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.NormalCompletion);
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
                    NextFilePosition = null;

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
        public static void Pause(long? filePosition = null)
        {
            Code.CancelPending(CodeChannel.File);
            IsPaused = true;

            if (filePosition != null)
            {
                _file.Position = filePosition.Value;
                _logger.Debug("Print has been paused at byte {0}", filePosition);
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
            NextFilePosition = null;
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
