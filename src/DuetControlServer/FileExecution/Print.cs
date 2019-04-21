using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Implementation of a file being printed
    /// </summary>
    public static class Print
    {
        private static AsyncLock _lock = new AsyncLock();
        private static BaseFile _file;
        private static bool _isPaused;
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
                _isPaused = false;
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

            // Process every available code
            for (Code code = await file.ReadCode(); code != null; code = await file.ReadCode())
            {
                // Has the file been paused? If so, rewind to the pause position
                bool paused = false;
                using (await _lock.LockAsync())
                {
                    if (_isPaused)
                    {
                        file.Seek(_pausePosition);
                        paused = true;
                    }
                }

                // Wait for print to resume
                if (paused)
                {
                    await _resumeEvent.WaitAsync(Program.CancelSource.Token);
                }

                // Execute the next command
                CodeResult result = await code.Execute();
                await Model.Provider.Output(result);
            }

            // Notify the controller that the print has finished
            SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.NormalCompletion);
        }

        /// <summary>
        /// Called when the file print has been paused
        /// </summary>
        /// <param name="filePosition">Position at which the file was paused</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Paused(uint filePosition)
        {
            using (await _lock.LockAsync())
            {
                _pausePosition = filePosition;
                _isPaused = true;
            }
        }

        /// <summary>
        /// Resume a paused print
        /// </summary>
        public static void Resume()
        {
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

                if (_isPaused)
                {
                    Resume();
                }
            }
        }
    }
}
