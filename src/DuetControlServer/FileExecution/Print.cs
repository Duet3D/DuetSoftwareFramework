using DuetAPI;
using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly AsyncLock _lock = new AsyncLock();
        private static BaseFile _file;
        private static long _pausePosition;
        private static readonly AsyncAutoResetEvent _resumeEvent = new AsyncAutoResetEvent();

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
                IsPaused = false;
            }

            // Wait for all pending firmware codes on the source channel to finish first
            await SPI.Interface.Flush(source);

            // Reset the resume event
            if (_resumeEvent.IsSet)
            {
                await _resumeEvent.WaitAsync();
            }

            // Analyze it and update the object model
            ParsedFileInfo info = await FileInfoParser.Parse(fileName);
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.Channels[CodeChannel.File].VolumetricExtrusion = false;
                Model.Provider.Get.Job.File.Assign(info);
            }

            // Notify RepRapFirmware and start processing the file
            Console.WriteLine($"[info] Printing file '{fileName}'");
            SPI.Interface.SetPrintStarted();
            RunPrint();
            return new CodeResult();
        }

        private static async void RunPrint()
        {
            BaseFile file = _file;

            // Process "start.g" at the beginning of a print
            string startPath = await FilePath.ToPhysical("start.g", "sys");
            if (File.Exists(startPath))
            {
                MacroFile startMacro = new MacroFile(startPath, CodeChannel.File, null);
                do
                {
                    Code code = startMacro.ReadCode();
                    if (code == null)
                    {
                        break;
                    }

                    try
                    {
                        CodeResult result = await code.Execute();
                        await Model.Provider.Output(result);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[err] {code} -> {e}");
                    }
                } while (!Program.CancelSource.IsCancellationRequested);
            }

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

                    // The trick here is that Code.Enqueue runs synchronously and returns a
                    // task instance that completes when a code result is available...
                    codes.Enqueue(code);
                    codeTasks.Enqueue(code.Enqueue());
                }

                // Is there anything to do?
                if (codes.TryDequeue(out code))
                {
                    // Keep track of the next file position so we know where to resume in case the print is paused while a macro is being performed 
                    if (codes.TryPeek(out Code nextCode))
                    {
                        LastFilePosition = nextCode.FilePosition;
                    }
                    else
                    {
                        LastFilePosition = file.Position;
                    }

                    // Process the next code
                    try
                    {
                        CodeResult result = await codeTasks.Dequeue();
                        await Model.Provider.Output(result);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[err] {code} -> {e}");
                    }
                }
                else
                {
                    // No more codes available - print must have finished
                    break;
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);

            Console.WriteLine("[info] DCS has finished printing");

            // Notify the controller that the print has stopped
            SPI.Communication.PrintStoppedReason stopReason = !file.IsAborted ? SPI.Communication.PrintStoppedReason.NormalCompletion
                : (IsPaused ? SPI.Communication.PrintStoppedReason.UserCancelled : SPI.Communication.PrintStoppedReason.Abort);
            SPI.Interface.SetPrintStopped(stopReason);

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

            await Model.Provider.Output(MessageType.Success, $"Print has been paused at byte {filePosition}");
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
        /// Called when the file print is supposed to be cancelled
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
    }
}
