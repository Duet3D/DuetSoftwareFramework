using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Main class dealing with job files
    /// </summary>
    /// <remarks>
    /// Lock this class whenever it is accessed (except for <see cref="Diagnostics(StringBuilder)"/>)
    /// </remarks>
    public static class JobProcessor
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
        /// Physical filename of the job file
        /// </summary>
        private static string _physicalFileName = string.Empty;

        /// <summary>
        /// First job file being read from
        /// </summary>
        private static CodeFile? _file;

        /// <summary>
        /// Second job file being read from
        /// </summary>
        private static CodeFile? _file2;

        /// <summary>
        /// Internal cancellation token source used to cancel pending codes when necessary
        /// </summary>
        private static CancellationTokenSource _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

        /// <summary>
        /// Indicates if a file has been selected for printing
        /// </summary>
        public static bool IsFileSelected => _file is not null;

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
        /// <param name="motionSystem">Motion system</param>
        /// <returns>File position</returns>
        public static async Task<long> GetFilePosition(int motionSystem)
        {
            if (_file is not null && motionSystem == 0)
            {
                using (await _file.LockAsync())
                {
                    return _file.Position;
                }
            }

            if (_file2 is not null && motionSystem == 1)
            {
                using (await _file2.LockAsync())
                {
                    return _file2.Position;
                }
            }

            return 0;
        }

        /// <summary>
        /// Dictionary of codes vs. synchronization tasks
        /// </summary>
        private static readonly Dictionary<Code, TaskCompletionSource<bool>> _syncRequests = new();

        /// <summary>
        /// Synchronize the File and File2 code streams, may only be called when a job is live
        /// </summary>
        /// <param name="code">Code to synchronize at</param>
        /// <returns>True if the sync request was successful, false otherwise</returns>
        /// <remarks>
        /// This must be called while the Job class is NOT locked and it must be called from the same
        /// code on File *AND* File2, else the sync request is never resolved (or at least not before the file is cancelled)
        /// </remarks>
        public static Task<bool> DoSync(Code code)
        {
            if (!code.IsFromFileChannel)
            {
                throw new ArgumentException("Code is not from a file channel");
            }
            if (code.FilePosition is null)
            {
                throw new ArgumentException("Code has no file position and cannot be used for sync requests", nameof(code));
            }

            if (_file2 is null)
            {
                // There is nothing to sync if there is only one file stream...
                return Task.FromResult(true);
            }

            lock (_syncRequests)
            {
                foreach (Code item in _syncRequests.Keys)
                {
                    if (code.Channel != item.Channel && code.FilePosition == item.FilePosition)
                    {
                        _syncRequests[item].TrySetResult(true);
                        return Task.FromResult(true);
                    }
                }

                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _syncRequests.Add(code, tcs);
                return tcs.Task;
            }
        }

        /// <summary>
        /// Set the current file position
        /// </summary>
        /// <param name="motionSystem">Motion system</param>
        /// <param name="filePosition">New file position</param>
        /// <returns>File position</returns>
        public static async Task SetFilePosition(int motionSystem, long filePosition)
        {
            if (_file is not null && motionSystem == 0)
            {
                using (await _file.LockAsync())
                {
                    _file.Position = filePosition;
                }
            }

            if (_file2 is not null && motionSystem == 1)
            {
                using (await _file2.LockAsync())
                {
                    _file2.Position = filePosition;
                }
            }

            lock (_syncRequests)
            {
                foreach (Code code in _syncRequests.Keys.ToList())
                {
                    if (code.FilePosition >= filePosition)
                    {
                        _syncRequests[code].TrySetResult(false);
                        _syncRequests.Remove(code);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the length of the file being printed in bytes
        /// </summary>
        public static long FileLength => _file is not null ? _file.Length : 0;

        /// <summary>
        /// Start a new file print
        /// </summary>
        /// <param name="fileName">File to print</param>
        /// <param name="physicalFile">Physical file to print</param>
        /// <param name="simulating">Whether the file is being simulated</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// This class has to be locked when this method is called
        /// </remarks>
        public static async Task SelectFile(string fileName, string physicalFile, bool simulating = false)
        {
            // Analyze and open the file
            GCodeFileInfo info = await InfoParser.Parse(physicalFile, true);

            bool supportsAyncMoves;
            using (await Provider.AccessReadOnlyAsync())
            {
                supportsAyncMoves = Provider.Get.Inputs[CodeChannel.File2] is not null;
            }
            CodeFile file = new(fileName, physicalFile, CodeChannel.File);
            CodeFile? file2 = supportsAyncMoves ? new(fileName, physicalFile, CodeChannel.File2) : null;

            // A file being printed may start another file print
            if (IsFileSelected)
            {
                await CancelAsync();
                await _finished.WaitAsync(Program.CancellationToken);
            }

            // Update the state
            IsCancelled = IsAborted = false;
            IsSimulating = simulating;
            _physicalFileName = physicalFile;
            _file = file;
            _file2 = file2;
            _pausePosition = null;

            // Update the object model
            using (await Provider.AccessReadWriteAsync())
            {
                Provider.Get.Job.File.Assign(info);
            }

            // Notify RepRapFirmware and start processing the file in the background
            await SPI.Interface.SetPrintFileInfo();
            _logger.Info("Selected file {0}", fileName);
        }

        /// <summary>
        /// Print from the given file and send resulting codes to the specified channel
        /// </summary>
        /// <param name="file">File to read from</param>
        /// <returns>Asynchronous task</returns>
        private static async Task DoFilePrint(CodeFile file)
        {
            // Get the cancellation token
            CancellationToken cancellationToken;
            using (await LockAsync())
            {
                cancellationToken = _cancellationTokenSource.Token;
            }

            // Use a code pool for print files
            Queue<Code> codePool = new();
            for (int i = 0; i < Math.Max(Settings.BufferedPrintCodes, 1); i++)
            {
                codePool.Enqueue(new Code());
            }

            // Assign the job file so flush requests are properly handled
            Codes.Processor.SetJobFile(file.Channel, file);

            // Wait for the object model to be updated.
            // This is necessary to determine if the File and File2 channels are active or not
            await Provider.WaitForUpdateAsync(cancellationToken);

            // Process the file being printed
            Queue<Code> codes = new();
            long nextFilePosition = 0;
            do
            {
                // Fill up the code buffer
                while (codePool.TryDequeue(out Code? sharedCode))
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
                    Code? readCode = null;
                    try
                    {
                        try
                        {
                            readCode = await file.ReadCodeAsync(sharedCode);
                            if (readCode is null)
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

                        readCode.Flags |= CodeFlags.Asynchronous;
                        codes.Enqueue(readCode);
                        await readCode.Execute();
                    }
                    catch (Exception e)
                    {
                        if (e is not OperationCanceledException)
                        {
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException!;
                            }
                            await Logger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {readCode?.LineNumber}: {e.Message}");
                            _logger.Error(e);
                        }
                        await AbortAsync();
                    }
                }

                // Is there anything more to do?
                if (codes.TryDequeue(out Code? code))
                {
                    try
                    {
                        try
                        {
                            // Logging of regular messages is done by the code itself, no need to take care of it here
                            await code.Task;
                            nextFilePosition = code.FilePosition ?? 0 + code.Length ?? 0;
                        }
                        catch (OperationCanceledException)
                        {
                            // Code has been cancelled, don't log this. This can happen when the file being printed is exchanged, when a
                            // pausable macro is interrupted, or when a code interceptor attempted to intercept a code on an inactive channel
                        }
                        catch (Exception e)
                        {
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException!;
                            }
                            await Logger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {code.LineNumber ?? 0}: {e.Message}");
                            _logger.Warn(e);
                        }
                    }
                    finally
                    {
                        // Code has finished, add it back to the code pool
                        codePool.Enqueue(code);
                    }
                }
                else
                {
                    // Flush one last time in case plugins inserted codes at the end of a print file
                    try
                    {
                        await Codes.Processor.FlushAsync(file);
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
                            await SetFilePosition(file.Channel == CodeChannel.File ? 0 : 1, newFilePosition);
                            _logger.Info("Job on {0} has been paused at byte {1}, reason {2}", file.Channel, newFilePosition, _pauseReason);

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

            // No longer printing
            Codes.Processor.SetJobFile(file.Channel, null);
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
                using (await LockAsync())
                {
                    await _resume.WaitAsync(Program.CancellationToken);
                    startingNewPrint = !_file!.IsClosed;
                    IsProcessing = startingNewPrint;
                }

                // Deal with the file print
                if (startingNewPrint)
                {
                    _logger.Info("Starting file print");

                    // Run the same file print on two distinct channels
                    Task firstFileTask = DoFilePrint(_file!);
                    Task secondFileTask = (_file2 is not null) ? DoFilePrint(_file2) : Task.CompletedTask;
                    await Task.WhenAll(firstFileTask, secondFileTask);

                    // Deal with the print result
                    using (await LockAsync())
                    {
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
                                    if (Provider.Get.State.UpTime < upTime || Provider.Get.Job.LastDuration is not null)
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
                                await InfoParser.UpdateSimulatedTime(_physicalFileName, lastDuration.Value);
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

                    // Clean up pending sync requests
                    lock (_syncRequests)
                    {
                        foreach (Code code in _syncRequests.Keys)
                        {
                            _syncRequests[code].TrySetResult(false);
                        }
                        _syncRequests.Clear();
                    }

                    // Dispose the file
                    _file!.Dispose();
                    _file2?.Dispose();
                    _file = _file2 = null;
                    _physicalFileName = string.Empty;

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

                using (_file!.Lock())
                {
                    _file.Close();
                }
                if (_file2 is not null)
                {
                    using (_file2.Lock())
                    {
                        _file2.Close();
                    }
                }
                IsCancelled = IsPaused;
                Resume();
            }
        }

        /// <summary>
        /// Cancel the current print (e.g. when M0/M1/M2 is called)
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task CancelAsync()
        {
            if (IsFileSelected)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                using (await _file!.LockAsync())
                {
                    _file.Close();
                }
                if (_file2 is not null)
                {
                    using (await _file2.LockAsync())
                    {
                        _file2.Close();
                    }
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

                using (_file!.Lock())
                {
                    _file.Close();
                }
                if (_file2 is not null)
                {
                    using (_file2.Lock())
                    {
                        _file2.Close();
                    }
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

                using (await _file!.LockAsync())
                {
                    _file.Close();
                }
                if (_file2 is not null)
                {
                    using (await _file2.LockAsync())
                    {
                        _file2.Close();
                    }
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
            IDisposable? lockObject = null;
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
                builder.Append($"File {_file!.FileName} is selected");
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
