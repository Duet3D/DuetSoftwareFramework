using DuetAPI;
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

        private static readonly AsyncLock _lock = new AsyncLock();
        private static FileStream _fileStream;
        private static StreamWriter _writer;

        /// <summary>
        /// Start logging to a file
        /// </summary>
        /// <param name="filename">Filename to write to</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Start(string filename)
        {
            using (_lock.Lock())
            {
                // Close any open file
                await StopInternal();

                // Start logging to the specified file
                _fileStream = new FileStream(filename, FileMode.Append, FileAccess.Write);
                _writer = new StreamWriter(_fileStream) { AutoFlush = true };
                await _writer.WriteLineAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Event logging started");
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
        }

        /// <summary>
        /// Write a message including timestamp to the log file
        /// </summary>
        /// <param name="msg">Message to log</param>
        public static async Task Log(Message msg)
        {
            using (await _lock.LockAsync())
            {
                if (_writer != null)
                {
                    try
                    {
                        await _writer.WriteAsync(msg.Time.ToString("yyyy-MM-dd HH:mm:ss "));
                        await _writer.WriteLineAsync(msg.ToString());
                    }
                    catch (AggregateException ae)
                    {
                        Console.Write("[err] Failed to write to log file: ");
                        Console.WriteLine(ae.InnerException);
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
        /// <returns></returns>
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
