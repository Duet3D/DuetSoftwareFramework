using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.IO;
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
        private static AsyncAutoResetEvent _resumeEvent = new AsyncAutoResetEvent();

        /// <summary>
        /// Begin a file print
        /// </summary>
        /// <param name="fileName">File to print</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Start(string fileName)
        {
            // Initialize the file
            using (await _lock.LockAsync())
            {
                if (_file != null)
                {
                    throw new InvalidOperationException("A file is already being printed");
                }
                _file = new BaseFile(fileName, DuetAPI.CodeChannel.File);
                IsPaused = false;
            }

            // Reset the resume event
            if (_resumeEvent.IsSet)
            {
                await _resumeEvent.WaitAsync();
            }

            // Analyze it and update the object model
            DuetAPI.ParsedFileInfo info = await FileInfoParser.Parse(fileName);
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.Job.File = info;
            }

            // Notify RepRapFirmware and start processing the file
            SPI.Interface.SetPrintStarted();
            RunPrint();
        }

        private static async void RunPrint()
        {
            BaseFile file = _file;

            // Process "start.g" at the beginning of a print
            string startPath = await FilePath.ToPhysical("sys/start.g");
            if (File.Exists(startPath))
            {
                MacroFile startMacro = new MacroFile(startPath, DuetAPI.CodeChannel.File, false, 0);
                do
                {
                    Code code = await startMacro.ReadCode();
                    if (code == null)
                    {
                        break;
                    }
                    await code.Execute();
                } while (!Program.CancelSource.IsCancellationRequested);
            }

            // Process the job file
            do
            {
                // Has the file been paused? If so, rewind to the pause position
                bool paused = false;
                using (await _lock.LockAsync())
                {
                    if (IsPaused)
                    {
                        file.Position = _pausePosition;
                        paused = true;
                    }
                }

                // Wait for print to resume
                if (paused)
                {
                    await _resumeEvent.WaitAsync(Program.CancelSource.Token);
                }

                // Execute the next command
                Code code = await file.ReadCode();
                if (code == null)
                {
                    break;
                }

                CodeResult result = await code.Execute();
                await Model.Provider.Output(result);
            }
            while (!Program.CancelSource.IsCancellationRequested);

            // Notify the controller that the print has stopped
            SPI.Communication.PrintStoppedReason stopReason = !file.IsAborted ? SPI.Communication.PrintStoppedReason.NormalCompletion
                : (IsPaused ? SPI.Communication.PrintStoppedReason.UserCancelled : SPI.Communication.PrintStoppedReason.Abort);
            SPI.Interface.SetPrintStopped(stopReason);

            // Invalidate the file being printed
            _file = null;
        }

        /// <summary>
        /// Reports the current file position
        /// </summary>
        public static long Position {
            get => _file.Position;
            set => _file.Position = value;
        }

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
        /// Called when the file print has been paused
        /// </summary>
        /// <param name="filePosition">Position at which the file was paused</param>
        /// <returns>Asynchronous task</returns>
        public static async Task OnPause(long filePosition)
        {
            using (await _lock.LockAsync())
            {
                _pausePosition = filePosition;
                IsPaused = true;
            }
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
