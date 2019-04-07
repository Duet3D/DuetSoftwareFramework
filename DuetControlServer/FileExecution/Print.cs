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
            lock (_file)
            {
                if (_file != null)
                {
                    throw new InvalidOperationException("A file is already being printed");
                }
                _file = new BaseFile(fileName, DuetAPI.CodeChannel.File);
                _isPaused = false;
            }

            // Reset the pause variables
            _isPaused = false;
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
            // Process every available code
            for (Code code = await _file.ReadCode(); code != null; code = await _file.ReadCode())
            {
                // Has the file been paused? If so, rewind to the pause position and wait for it to resume
                if (_isPaused)
                {
                    _file.Seek(_pausePosition);
                    await _resumeEvent.WaitAsync(Program.CancelSource.Token);
                }

                // Execute the next command
                CodeResult result = (CodeResult)await code.Execute();
                await Model.Provider.Output(result);
            }

            // Notify the controller that the print has finished
            SPI.Interface.SetPrintStopped(SPI.Communication.PrintStoppedReason.NormalCompletion);
        }

        /// <summary>
        /// Called when the file print has been paused
        /// </summary>
        /// <param name="filePosition"></param>
        public static void Paused(uint filePosition)
        {
            _pausePosition = filePosition;
            _isPaused = true;
        }

        /// <summary>
        /// Resume a paused print
        /// </summary>
        public static void Resume()
        {
            _resumeEvent.Set();
        }

        /// <summary>
        /// Called when the file print has been cancelled
        /// </summary>
        public static void Cancel()
        {
            if (_file != null)
            {
                _file.Abort();
                _resumeEvent.Set();
            }
        }
    }
}
