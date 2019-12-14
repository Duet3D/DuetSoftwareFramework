using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Implementation of a file being printed
    /// </summary>
    public static class Print
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Lock for thread-safe access
        /// </summary>
        private static readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Job file being read from
        /// </summary>
        private static BaseFile _file;

        /// <summary>
        /// Position in bytes at which the print was paused
        /// </summary>
        private static long _pausePosition;

        /// <summary>
        /// Event to trigger when the job is supposed to be resumed. It is also triggered after a print has been cancelled
        /// </summary>
        private static readonly AsyncManualResetEvent _resumeEvent = new AsyncManualResetEvent();

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            using (await _lock.LockAsync())
            {
                if (_file != null)
                {
                    builder.AppendLine($"Processing print job {_file.FileName}");
                }
            }
        }

        /// <summary>
        /// Begin a file print
        /// </summary>
        /// <param name="fileName">File to print</param>
        /// <param name="source">Channel that requested the file to be printed</param>
        /// <returns>Code result</returns>
        public static async Task<CodeResult> Start(string fileName, CodeChannel source)
        {
            // Initialize the file
            using (await _lock.LockAsync())
            {
                if (_file != null)
                {
                    return new CodeResult(MessageType.Error, "A file is already being printed");
                }

                _file = new BaseFile(fileName, CodeChannel.File);
                IsPaused = IsAborted = false;
            }

            // Analyze it and update the object model
            ParsedFileInfo info = await FileInfoParser.Parse(fileName);
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Channels[CodeChannel.File].VolumetricExtrusion = false;
                Model.Provider.Get.Job.File.Assign(info);
            }

            // Notify RepRapFirmware and start processing the file in the background
            _logger.Info("Printing file {0}", fileName);
            SPI.Interface.SetPrintStarted();
            _resumeEvent.Reset();
            RunPrint();

            // Return a result
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Channels[source].Compatibility == Compatibility.Marlin)
                {
                    return new CodeResult(MessageType.Success, "File opened\nFile selected");
                }
                else
                {
                    return new CodeResult();
                }
            }
        }

        /// <summary>
        /// Perform the actual print job
        /// </summary>
        private static async void RunPrint()
        {
            BaseFile file = _file;

            // Process the job file
            Queue<Code> codes = new Queue<Code>();
            Queue<Task<CodeResult>> codeTasks = new Queue<Task<CodeResult>>();
            do
            {
                // Has the file been paused? If so, rewind to the pause position
                bool paused = false;
                using (await _lock.LockAsync())
                {
                    if (!file.IsFinished && IsPaused)
                    {
                        file.Position = _pausePosition;
                        paused = true;
                    }
                }

                // Wait for print to resume
                if (paused)
                {
                    codes.Clear();
                    codeTasks.Clear();

                    await _resumeEvent.WaitAsync(Program.CancelSource.Token);
                    _resumeEvent.Reset();

                    if (Program.CancelSource.IsCancellationRequested)
                    {
                        // Stop if the program is being terminated
                        return;
                    }
                }

                // Fill up the code buffer
                Code code;
                while (codeTasks.Count == 0 || codeTasks.Count < Settings.BufferedPrintCodes)
                {
                    code = file.ReadCode();
                    if (code == null)
                    {
                        break;
                    }

                    codes.Enqueue(code);
                    codeTasks.Enqueue(code.Execute());
                }

                // Is there anything to do?
                if (codes.TryDequeue(out code))
                {
                    // Keep track of the next file position so we know where to resume in case the print is paused while a macro is being performed 
                    LastFilePosition = (codes.TryPeek(out Code nextCode)) ? nextCode.FilePosition : file.Position;

                    // Wait the next code to finish
                    try
                    {
                        CodeResult result = await codeTasks.Dequeue();
                        await Utility.Logger.LogOutput(result);
                    }
                    catch (Exception e)
                    {
                        if (!(e is OperationCanceledException))
                        {
                            await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} threw an exception: [{e.GetType().Name}] {e.Message}");
                            await Abort();
                        }
                    }
                }
                else
                {
                    // No more codes available - print must have finished
                    break;
                }
            } while (!Program.CancelSource.IsCancellationRequested);

            // Update the last print filename
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Job.LastFileAborted = file.IsAborted && IsAborted;
                Model.Provider.Get.Job.LastFileCancelled = file.IsAborted && !IsAborted;
                Model.Provider.Get.Job.LastFileName = await FilePath.ToVirtualAsync(file.FileName);
                // FIXME: Add support for simulation
            }

            // Notify the controller that the print has stopped
            if (file.IsAborted)
            {
                Code.CancelPending(CodeChannel.File);
                if (IsAborted)
                {
                    _logger.Info("Aborted print");
                    SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.Abort);
                }
                else
                {
                    _logger.Info("Cancelled print");
                    SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.UserCancelled);
                }
            }
            else
            {
                _logger.Info("Finished print");
                SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.NormalCompletion);
            }

            // Invalidate the file being printed
            _file = null;
            LastFilePosition = null;
        }

        /// <summary>
        /// Reports the current file position
        /// </summary>
        public static long Position {
            get => _file.Position;
            set => _file.Position = value;
        }

        /// <summary>
        /// Holds the file position after the current code being executed
        /// </summary>
        public static long? LastFilePosition { get; private set; }

        /// <summary>
        /// Returns the length of the file being printed in bytes
        /// </summary>
        public static long Length { get => _file.Length; }

        /// <summary>
        /// Indicates if a print is going on
        /// </summary>
        public static bool IsPrinting { get => _file != null; }

        /// <summary>
        /// Indicates if the file print has been paused
        /// </summary>
        public static bool IsPaused { get; private set; }

        /// <summary>
        /// Indicates if the file print has been aborted
        /// </summary>
        public static bool IsAborted { get; private set; }

        /// <summary>
        /// Called when the print is being paused
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Pause()
        {
            using (await _lock.LockAsync())
            {
                IsPaused = true;
            }
        }

        /// <summary>
        /// Called when the file print has been paused
        /// </summary>
        /// <param name="filePosition">Position at which the file was paused</param>
        /// <returns>Asynchronous task</returns>
        public static async Task OnPause(long filePosition)
        {
            using (await _lock.LockAsync())
            {
                _pausePosition = filePosition;
                if (IsPaused)
                {
                    _file.Position = filePosition;
                }
                else
                {
                    IsPaused = true;
                }
            }

            await Utility.Logger.LogOutput(MessageType.Success, $"Print has been paused at byte {filePosition}");
        }

        /// <summary>
        /// Resume a paused print
        /// </summary>
        public static void Resume()
        {
            IsPaused = false;
            _resumeEvent.Set();
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0 is called)
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Cancel()
        {
            using (await _lock.LockAsync())
            {
                if (_file != null)
                {
                    _file.Abort();
                    _file = null;
                }

                if (IsPaused)
                {
                    Resume();
                }
            }
        }

        /// <summary>
        /// Abort the current print. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Abort()
        {
            using (await _lock.LockAsync())
            {
                if (_file != null)
                {
                    _file.Abort();
                    _file = null;
                }

                if (IsPaused)
                {
                    Resume();
                }
                IsAborted = true;
            }
        }
    }
}
