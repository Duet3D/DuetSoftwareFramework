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
        /// Monitor for the print tasks
        /// </summary>
        private static readonly AsyncMonitor _monitor = new AsyncMonitor();

        /// <summary>
        /// Lock this class asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public static AwaitableDisposable<IDisposable> LockAsync() => _monitor.EnterAsync();

        /// <summary>
        /// Job file being read from
        /// </summary>
        private static BaseFile _file;

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
        /// Returns the length of the file being printed in bytes
        /// </summary>
        public static long Length { get => _file.Length; }

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
        public static long? NextFilePosition { get; set; }

        /// <summary>
        /// Begin a file print
        /// </summary>
        /// <param name="fileName">File to print</param>
        /// <param name="source">Channel that requested the file to be printed</param>
        /// <returns>Code result</returns>
        public static async Task<CodeResult> Start(string fileName, CodeChannel source)
        {
            // Initialize the file
            if (_file != null)
            {
                if (source != CodeChannel.File)
                {
                    _logger.Info("Print is starting a new file");
                    return new CodeResult(MessageType.Error, "A file is already being printed");
                }

                // A file being printed may start another file print, deal with pending codes
                Abort();
            }

            // Reset the state and analyze this file
            IsPaused = IsAborted = false;
            _file = new BaseFile(fileName, CodeChannel.File);
            ParsedFileInfo info = await FileInfoParser.Parse(fileName);

            using (await Model.Provider.AccessReadWriteAsync())
            {
                // Update the object model
                Model.Provider.Get.Channels[CodeChannel.File].VolumetricExtrusion = false;
                Model.Provider.Get.Job.File.Assign(info);

                // Notify RepRapFirmware and start processing the file in the background
                _logger.Info("Printing file {0}", fileName);
                SPI.Interface.SetPrintStarted();
                _monitor.Pulse();

                // Return a result
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
        /// Perform actual print jobs
        /// </summary>
        public static async Task Run()
        {
            do
            {
                // Wait for the next print to start
                BaseFile file;
                using (await _monitor.EnterAsync())
                {
                    await _monitor.WaitAsync(Program.CancelSource.Token);
                    file = _file;
                }

                Queue<Code> codes = new Queue<Code>();
                Queue<Task<CodeResult>> codeTasks = new Queue<Task<CodeResult>>();
                do
                {
                    Code code;
                    using (await _monitor.EnterAsync())
                    {
                        // Check if the print is still going
                        if (_file != file && _file != null)
                        {
                            // No longer printing the file we started with
                            break;
                        }

                        if (IsPaused)
                        {
                            // Wait for print to resume
                            await _monitor.WaitAsync(Program.CancelSource.Token);
                        }

                        // Fill up the code buffer
                        while (codeTasks.Count == 0 || codeTasks.Count < Settings.BufferedPrintCodes)
                        {
                            code = file.ReadCode();
                            if (code == null)
                            {
                                // No more codes available
                                break;
                            }

                            codes.Enqueue(code);
                            codeTasks.Enqueue(code.Execute());
                        }
                    }

                    // Is there anything more to do?
                    if (codes.TryDequeue(out code))
                    {
                        // Wait for the next code to finish
                        try
                        {
                            CodeResult result = await codeTasks.Dequeue();
                            await Utility.Logger.LogOutput(result);
                        }
                        catch (Exception e)
                        {
                            if (!(e is OperationCanceledException))
                            {
                                await Utility.Logger.LogOutput(MessageType.Error, $"{code.ToShortString()} has thrown an exception: [{e.GetType().Name}] {e.Message}");
                                using (await _monitor.EnterAsync())
                                {
                                    Abort();
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // No more codes available - print must have finished
                        using (await _monitor.EnterAsync())
                        {
                            NextFilePosition = null;
                            _file = null;
                        }
                        break;
                    }
                } while (!Program.CancelSource.IsCancellationRequested && !IsAborted);

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

                // Dispose the file being printed
                file.Dispose();
            }
            while (!Program.CancelSource.IsCancellationRequested);
        }

        /// <summary>
        /// Called when the print is being paused
        /// </summary>
        /// <returns>Asynchronous task</returns>
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
        /// Resume a paused print
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Resume()
        {
            IsPaused = false;
            _monitor.Pulse();
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0 is called)
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Cancel()
        {
            Code.CancelPending(CodeChannel.File);
            NextFilePosition = null;

            if (_file != null)
            {
                _file.Abort();
                _file = null;
            }

            if (IsPaused)
            {
                IsPaused = false;
                _monitor.Pulse();
            }
        }

        /// <summary>
        /// Abort the current print. This is called when the print could not complete as expected
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Abort()
        {
            Code.CancelPending(CodeChannel.File);
            NextFilePosition = null;
            IsAborted = true;

            if (_file != null)
            {
                _file.Abort();
                _file = null;
            }

            if (IsPaused)
            {
                IsPaused = false;
                _monitor.Pulse();
            }
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            using (await _monitor.EnterAsync())
            {
                if (_file != null)
                {
                    builder.AppendLine($"Processing print job {_file.FileName}");
                }
            }
        }
    }
}
