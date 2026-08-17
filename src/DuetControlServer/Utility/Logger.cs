using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Utility;

/// <summary>
/// Class for message logging
/// </summary>
/// <param name="filePath">File path resolver</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
public class EventLogger(FilePathResolver filePath, Model.ObjectModel model, IHostApplicationLifetime lifetime, ILogger<EventLogger> logger, IOptions<Settings> settings)
{
    /// <summary>
    /// Default log file for M929 in case no P parameter is specified
    /// </summary>
    public const string DefaultLogFile = "eventlog.txt";

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
    public void Start(string filename, EventLogLevel level)
    {
        using (_lock.Lock(lifetime.ApplicationStopping))
        {
            // Close any open file
            StopInternal();

            // Initialize access to the log file
            string physicalFile = filePath.ToPhysical(filename, FileDirectory.System);
            _fileStream = new FileStream(physicalFile, FileMode.Append, FileAccess.Write, FileShare.Read, settings.Value.FileBufferSize);
            _writer = new StreamWriter(_fileStream, Encoding.UTF8, settings.Value.FileBufferSize) { AutoFlush = true };
            _logCloseEvent = lifetime.ApplicationStopping.Register(Stop);

            // Write the first line
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");

            // Update the object model
            using (model.AccessReadWrite())
            {
                model.State.LogFile = filename;
                model.State.LogLevel = level;
            }

            // Write event
            logger.LogInformation("Event logging to {File} started", filename);
        }
    }

    /// <summary>
    /// Start logging to a file
    /// </summary>
    /// <param name="filename">Filename to write to</param>
    /// <param name="level">Requested log level</param>
    /// <returns>Asynchronous task</returns>
    public async Task StartAsync(string filename, EventLogLevel level)
    {
        using (await _lock.LockAsync(lifetime.ApplicationStopping))
        {
            // Close any open file
            await StopInternalAsync();

            // Initialize access to the log file
            string physicalFile = await filePath.ToPhysicalAsync(filename, FileDirectory.System);
            _fileStream = new FileStream(physicalFile, FileMode.Append, FileAccess.Write, FileShare.Read, settings.Value.FileBufferSize);
            _writer = new StreamWriter(_fileStream, Encoding.UTF8, settings.Value.FileBufferSize) { AutoFlush = true };
            _logCloseEvent = lifetime.ApplicationStopping.Register(Stop);

            // Write the first line
            await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");

            // Update the object model
            using (await model.AccessReadWriteAsync())
            {
                model.State.LogFile = filename;
                model.State.LogLevel = level;
            }

            // Write event
            logger.LogInformation("Event logging to {File} started", filename);
        }
    }

    /// <summary>
    /// Stop logging
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public void Stop()
    {
        using (_lock.Lock(lifetime.ApplicationStopped))
        {
            StopInternal();
        }
    }

    /// <summary>
    /// Stop logging asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task StopAsync()
    {
        using (await _lock.LockAsync(lifetime.ApplicationStopped))
        {
            await StopInternalAsync();
        }
    }

    /// <summary>
    /// Stop logging internally
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void StopInternal()
    {
        if (_writer is not null)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging stopped");
            _writer.Close();
            _writer = null;

            logger.LogInformation("Event logging stopped");
        }

        if (_fileStream is not null)
        {
            _fileStream.Close();
            _fileStream = null;
        }

        if (!lifetime.ApplicationStopped.IsCancellationRequested)
        {
            if (_logCloseEvent is not null)
            {
                _logCloseEvent.Dispose();
                _logCloseEvent = null;
            }

            using (model.AccessReadWrite(lifetime.ApplicationStopped))
            {
                model.State.LogFile = null;
                model.State.LogLevel = EventLogLevel.Off;
            }
        }
    }

    /// <summary>
    /// Stop logging internally and asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private async Task StopInternalAsync()
    {
        if (_writer is not null)
        {
            await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging stopped");
            _writer.Close();
            _writer = null;

            logger.LogInformation("Event logging stopped");
        }

        if (_fileStream is not null)
        {
            _fileStream.Close();
            _fileStream = null;
        }

        if (!lifetime.ApplicationStopped.IsCancellationRequested)
        {
            if (_logCloseEvent is not null)
            {
                _logCloseEvent.Dispose();
                _logCloseEvent = null;
            }

            using (await model.AccessReadWriteAsync(lifetime.ApplicationStopped))
            {
                model.State.LogFile = null;
                model.State.LogLevel = EventLogLevel.Off;
            }
        }
    }

    /// <summary>
    /// Write a message including timestamp to the log file
    /// </summary>
    /// <param name="level">Log level of the message</param>
    /// <param name="message">Message to log</param>
    public void Log(EventLogLevel level, Message message)
    {
        using (_lock.Lock(lifetime.ApplicationStopping))
        {
            if (level != EventLogLevel.Off && _writer is not null && !string.IsNullOrWhiteSpace(message?.Content))
            {
                using (model.AccessReadOnly())
                {
                    if (model.State.LogLevel == EventLogLevel.Off || level < model.State.LogLevel)
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
                    logger.LogError(e, "Failed to write to log file");
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
    public async Task LogAsync(EventLogLevel level, Message message)
    {
        using (await _lock.LockAsync(lifetime.ApplicationStopping))
        {
            if (level != EventLogLevel.Off && _writer is not null && !string.IsNullOrWhiteSpace(message?.Content))
            {
                using (await model.AccessReadOnlyAsync())
                {
                    if (model.State.LogLevel == EventLogLevel.Off || level < model.State.LogLevel)
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
                    logger.LogError(e, "Failed to write to log file");
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
    public void Log(EventLogLevel level, MessageType type, string content) => Log(level, new Message(type, content));

    /// <summary>
    /// Write a message including timestamp to the log file asynchronously
    /// </summary>
    /// <param name="level">Log level</param>
    /// <param name="type">Message type</param>
    /// <param name="content">Message content</param>
    /// <returns>Asynchronous task</returns>
    public Task LogAsync(EventLogLevel level, MessageType type, string content) => LogAsync(level, new Message(type, content));

    /// <summary>
    /// Write a message including timestamp to the log file
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="content">Message content</param>
    public void Log(MessageType type, string content)
    {
        EventLogLevel level = (type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn;
        Log(level, new Message(type, content));
    }

    /// <summary>
    /// Write a message including timestamp to the log file asynchronously
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="content">Message content</param>
    /// <returns>Asynchronous task</returns>
    public async Task LogAsync(MessageType type, string content)
    {
        EventLogLevel level = (type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn;
        await LogAsync(level, new Message(type, content));
    }

    /// <summary>
    /// Write messages including timestamp to the log file
    /// </summary>
    /// <param name="message">Message to log</param>
    public void Log(Message message)
    {
        if (message is not null && !string.IsNullOrEmpty(message.Content))
        {
            EventLogLevel level = (message.Type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn;
            Log(level, message);
        }
    }

    /// <summary>
    /// Write messages including timestamp to the log file asynchronously
    /// </summary>
    /// <param name="message">Message to log</param>
    /// <returns>Asynchronous task</returns>
    public async Task LogAsync(Message message)
    {
        if (message is not null && !string.IsNullOrEmpty(message.Content))
        {
            EventLogLevel level = (message.Type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn;
            await LogAsync(level, message);
        }
    }

    /// <summary>
    /// Log and output a message
    /// </summary>
    /// <param name="message">Message to log and output</param>
    public void LogOutput(Message message)
    {
        if (message is not null && !string.IsNullOrEmpty(message.Content))
        {
            model.Output(message);
            Log((message.Type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn, message);
        }
    }

    /// <summary>
    /// Log and output a message asynchronously
    /// </summary>
    /// <param name="message">Message to log and output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task LogOutputAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (message is not null && !string.IsNullOrEmpty(message.Content))
        {
            await model.OutputAsync(message, cancellationToken);
            await LogAsync((message.Type == MessageType.Success) ? EventLogLevel.Info : EventLogLevel.Warn, message);
        }
    }

    /// <summary>
    /// Log and output a message
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="content">Message content</param>
    /// <returns>Asynchronous task</returns>
    public void LogOutput(MessageType type, string content) => LogOutput(new Message(type, content));

    /// <summary>
    /// Log and output a message
    /// </summary>
    /// <param name="type">Message type</param>
    /// <param name="content">Message content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task LogOutputAsync(MessageType type, string content, CancellationToken cancellationToken = default) => LogOutputAsync(new Message(type, content), cancellationToken);
}
