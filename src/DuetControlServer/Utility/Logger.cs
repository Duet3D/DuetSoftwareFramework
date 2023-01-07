using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using Nito.AsyncEx;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Class for message logging
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// Default log file for M929 in case no P parameter is specified
        /// </summary>
        public const string DefaultLogFile = "eventlog.txt";

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Lock for the file
        /// </summary>
        private static readonly AsyncLock _lock = new();

        /// <summary>
        /// File stream of the log file
        /// </summary>
        private static FileStream? _fileStream;

        /// <summary>
        /// Writer for logging data
        /// </summary>
        private static StreamWriter? _writer;

        /// <summary>
        /// Registration that is triggered when the log is supposed to be closed
        /// </summary>
        private static IDisposable? _logCloseEvent;

        /// <summary>
        /// Start logging to a file
        /// </summary>
        /// <param name="filename">Filename to write to</param>
        /// <param name="level">Requested log level</param>
        /// <returns>Asynchronous task</returns>
        public static void Start(string filename, LogLevel level)
        {
            using (_lock.Lock(Program.CancellationToken))
            {
                // Close any open file
                StopInternal();

                // Initialize access to the log file
                string physicalFile = FilePath.ToPhysical(filename, FileDirectory.System);
                _fileStream = new FileStream(physicalFile, FileMode.Append, FileAccess.Write, FileShare.Read, Settings.FileBufferSize);
                _writer = new StreamWriter(_fileStream, Encoding.UTF8, Settings.FileBufferSize) { AutoFlush = true };
                _logCloseEvent = Program.CancellationToken.Register(Stop);

                // Write the first line
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");

                // Update the object model
                using (Model.Provider.AccessReadWrite())
                {
                    Model.Provider.Get.State.LogFile = filename;
                    Model.Provider.Get.State.LogLevel = level;
                }

                // Write event
                _logger.Info("Event logging to {0} started", filename);
            }
        }

        /// <summary>
        /// Start logging to a file
        /// </summary>
        /// <param name="filename">Filename to write to</param>
        /// <param name="level">Requested log level</param>
        /// <returns>Asynchronous task</returns>
        public static async Task StartAsync(string filename, LogLevel level)
        {
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                // Close any open file
                await StopInternalAsync();

                // Initialize access to the log file
                string physicalFile = await FilePath.ToPhysicalAsync(filename, FileDirectory.System);
                _fileStream = new FileStream(physicalFile, FileMode.Append, FileAccess.Write, FileShare.Read, Settings.FileBufferSize);
                _writer = new StreamWriter(_fileStream, Encoding.UTF8, Settings.FileBufferSize) { AutoFlush = true };
                _logCloseEvent = Program.CancellationToken.Register(Stop);

                // Write the first line
                await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");

                // Update the object model
                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.State.LogFile = filename;
                    Model.Provider.Get.State.LogLevel = level;
                }

                // Write event
                _logger.Info("Event logging to {0} started", filename);
            }
        }

        /// <summary>
        /// Stop logging
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static void Stop()
        {
            using (_lock.Lock(Program.CancellationToken))
            {
                StopInternal();
            }
        }

        /// <summary>
        /// Stop logging asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task StopAsync()
        {
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                await StopInternalAsync();
            }
        }

        /// <summary>
        /// Stop logging internally
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void StopInternal()
        {
            if (_writer is not null)
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging stopped");
                _writer.Close();
                _writer = null;

                _logger.Info("Event logging stopped");
            }

            if (_fileStream is not null)
            {
                _fileStream.Close();
                _fileStream = null;
            }

            if (!Program.CancellationToken.IsCancellationRequested)
            {
                if (_logCloseEvent is not null)
                {
                    _logCloseEvent.Dispose();
                    _logCloseEvent = null;
                }

                using (Model.Provider.AccessReadWrite())
                {
                    Model.Provider.Get.State.LogFile = null;
                    Model.Provider.Get.State.LogLevel = LogLevel.Off;
                }
            }
        }

        /// <summary>
        /// Stop logging internally and asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task StopInternalAsync()
        {
            if (_writer is not null)
            {
                await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging stopped");
                _writer.Close();
                _writer = null;

                _logger.Info("Event logging stopped");
            }

            if (_fileStream is not null)
            {
                _fileStream.Close();
                _fileStream = null;
            }

            if (!Program.CancellationToken.IsCancellationRequested)
            {
                if (_logCloseEvent is not null)
                {
                    _logCloseEvent.Dispose();
                    _logCloseEvent = null;
                }

                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.State.LogFile = null;
                    Model.Provider.Get.State.LogLevel = LogLevel.Off;
                }
            }
        }

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="level">Log level of the message</param>
        /// <param name="message">Message to log</param>
        public static void Log(LogLevel level, Message message)
        {
            using (_lock.Lock(Program.CancellationToken))
            {
                if (level != LogLevel.Off && _writer is not null && !string.IsNullOrWhiteSpace(message?.Content))
                {
                    using (Model.Provider.AccessReadOnly())
                    {
                        if (Model.Provider.Get.State.LogLevel == LogLevel.Off || level < Model.Provider.Get.State.LogLevel)
                        {
                            return;
                        }
                    }

                    try
                    {
                        _writer.Write(message.Time.ToString("yyyy-MM-dd HH:mm:ss "));
                        _writer.WriteLine(message.ToString().TrimEnd());
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to write to log file");
                        StopInternal();
                    }
                }
            }
        }

        /// <summary>
        /// Write a message including timestamp to the log file asynchronously
        /// </summary>
        /// <param name="level">Log level of the message</param>
        /// <param name="message">Message to log</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogAsync(LogLevel level, Message message)
        {
            using (await _lock.LockAsync(Program.CancellationToken))
            {
                if (level != LogLevel.Off && _writer is not null && !string.IsNullOrWhiteSpace(message?.Content))
                {
                    using (await Model.Provider.AccessReadOnlyAsync())
                    {
                        if (Model.Provider.Get.State.LogLevel == LogLevel.Off || level < Model.Provider.Get.State.LogLevel)
                        {
                            return;
                        }
                    }

                    try
                    {
                        await _writer.WriteAsync(message.Time.ToString("yyyy-MM-dd HH:mm:ss "));
                        await _writer.WriteLineAsync(message.ToString().TrimEnd());
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to write to log file");
                        await StopInternalAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public static void Log(LogLevel level, MessageType type, string content) => Log(level, new Message(type, content));

        /// <summary>
        /// Write a message including timestamp to the log file asynchronously
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        /// <returns>Asynchronous task</returns>
        public static Task LogAsync(LogLevel level, MessageType type, string content) => LogAsync(level, new Message(type, content));

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public static void Log(MessageType type, string content)
        {
            LogLevel level = (type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn;
            Log(level, new Message(type, content));
        }

        /// <summary>
        /// Write a message including timestamp to the log file asynchronously
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogAsync(MessageType type, string content)
        {
            LogLevel level = (type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn;
            await LogAsync(level, new Message(type, content));
        }

        /// <summary>
        /// Write messages including timestamp to the log file
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void Log(Message message)
        {
            if (message is not null && !string.IsNullOrEmpty(message.Content))
            {
                LogLevel level = (message.Type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn;
                Log(level, message);
            }
        }

        /// <summary>
        /// Write messages including timestamp to the log file asynchronously
        /// </summary>
        /// <param name="message">Message to log</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogAsync(Message message)
        {
            if (message is not null && !string.IsNullOrEmpty(message.Content))
            {
                LogLevel level = (message.Type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn;
                await LogAsync(level, message);
            }
        }

        /// <summary>
        /// Log and output a message
        /// </summary>
        /// <param name="message">Message to log and output</param>
        public static void LogOutput(Message message)
        {
            if (message is not null && !string.IsNullOrEmpty(message.Content))
            {
                Model.Provider.Output(message);
                Log((message.Type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn, message);
            }
        }

        /// <summary>
        /// Log and output a message asynchronously
        /// </summary>
        /// <param name="message">Message to log and output</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogOutputAsync(Message message)
        {
            if (message is not null && !string.IsNullOrEmpty(message.Content))
            {
                await Model.Provider.OutputAsync(message);
                await LogAsync((message.Type == MessageType.Success) ? LogLevel.Info : LogLevel.Warn, message);
            }
        }

        /// <summary>
        /// Log and output a message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        /// <returns>Asynchronous task</returns>
        public static void LogOutput(MessageType type, string content) => LogOutput(new Message(type, content));

        /// <summary>
        /// Log and output a message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        /// <returns>Asynchronous task</returns>
        public static Task LogOutputAsync(MessageType type, string content) => LogOutputAsync(new Message(type, content));
    }
}
