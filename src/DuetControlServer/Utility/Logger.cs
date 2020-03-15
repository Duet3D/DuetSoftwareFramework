﻿using DuetAPI;
using Nito.AsyncEx;
using System;
using System.IO;
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
        private static readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Filestream of the log file
        /// </summary>
        private static FileStream _fileStream;

        /// <summary>
        /// Writer for logging data
        /// </summary>
        private static StreamWriter _writer;

        /// <summary>
        /// Registration that is triggered when the log is supposed to be closed
        /// </summary>
        private static IDisposable _logCloseEvent;

        /// <summary>
        /// Start logging to a file
        /// </summary>
        /// <param name="filename">Filename to write to</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Start(string filename)
        {
            using (await _lock.LockAsync())
            {
                // Close any open file
                await StopInternal();

                // Initialize access to the log file
                _fileStream = new FileStream(filename, FileMode.Append, FileAccess.Write);
                _writer = new StreamWriter(_fileStream) { AutoFlush = true };
                _logCloseEvent = Program.CancellationToken.Register(Stop().Wait);

                // Write the first line
                await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");

                // Update the object model
                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.State.LogFile = filename;
                }
            }
        }

        /// <summary>
        /// Stop logging
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Stop()
        {
            using (await _lock.LockAsync())
            {
                await StopInternal();
            }
        }

        /// <summary>
        /// Stop logging internally
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task StopInternal()
        {
            if (_writer != null)
            {
                await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging stopped");
                _writer.Close();
                _writer = null;
            }

            if (_fileStream != null)
            {
                _fileStream.Close();
                _fileStream = null;
            }

            if (_logCloseEvent != null)
            {
                _logCloseEvent.Dispose();
                _logCloseEvent = null;
            }

            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.LogFile = null;
            }
        }

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="msg">Message to log</param>
        public static async Task Log(Message msg)
        {
            using (await _lock.LockAsync())
            {
                if (_writer != null && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    try
                    {
                        await _writer.WriteAsync(msg.Time.ToString("yyyy-MM-dd HH:mm:ss "));
                        await _writer.WriteLineAsync(msg.ToString());
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to write to log file");
                        await StopInternal();
                    }
                }
            }
        }

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        public static Task Log(MessageType type, string content) => Log(new Message(type, content));

        /// <summary>
        /// Write messages including timestamp to the log file
        /// </summary>
        /// <param name="result">Message list</param>
        public static async Task Log(DuetAPI.Commands.CodeResult result)
        {
            if (result != null)
            {
                foreach (Message msg in result)
                {
                    await Log(msg);
                }
            }
        }

        /// <summary>
        /// Log and output a message
        /// </summary>
        /// <param name="msg">Message</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogOutput(Message msg)
        {
            await Model.Provider.Output(msg);
            await Log(msg);
        }

        /// <summary>
        /// Log and output a code result
        /// </summary>
        /// <param name="result">Code result</param>
        /// <returns>Asynchronous task</returns>
        public static async Task LogOutput(DuetAPI.Commands.CodeResult result)
        {
            if (result != null)
            {
                foreach (Message msg in result)
                {
                    await LogOutput(msg);
                }
            }
        }

        /// <summary>
        /// Log and output a message
        /// </summary>
        /// <param name="type">Message type</param>
        /// <param name="content">Message content</param>
        /// <returns>Asynchronous task</returns>
        public static Task LogOutput(MessageType type, string content) => LogOutput(new Message(type, content));
    }
}
