using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;
using System.IO;
using System.Net.Sockets;
using DuetAPI.Commands;
using DuetAPI;
using DuetAPI.Connection.InitMessages;
using System.Text;
using DuetAPI.ObjectModel;
using DuetControlServer.SPI.Communication.Shared;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Command interpreter for code streams
    /// </summary>
    public sealed class CodeStream : Base
    {
        /// <summary>
        /// List of supported commands in this mode.
        /// This is not really used because this mode reads lines and no JSON objects
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Code)
        };

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// List of active subscribers
        /// </summary>
        private static readonly List<CodeStream> _streams = new();

        /// <summary>
        /// Check if there are any clients waiting for generic messages
        /// </summary>
        public static bool HasClientsWaitingForMessages
        {
            get
            {
                lock (_streams)
                {
                    foreach (CodeStream stream in _streams)
                    {
                        MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)stream._channel);
                        if (MessageTypeFlags.GenericMessage.HasFlag(channelFlag))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Maximum number of codes to execute simultaneously
        /// </summary>
        private readonly int _bufferSize;

        /// <summary>
        /// Code channel for incoming codes
        /// </summary>
        private readonly CodeChannel _channel;

        /// <summary>
        /// Lock for outputting data
        /// </summary>
        private readonly AsyncLock _outputLock = new();

        /// <summary>
        /// Stream for writing to a client
        /// </summary>
        private StreamWriter _streamWriter;

        /// <summary>
        /// Constructor of the code stream interpreter
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message from the client</param>
        public CodeStream(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            CodeStreamInitMessage codeStreamInitMessage = (CodeStreamInitMessage)initMessage;
            _bufferSize = codeStreamInitMessage.BufferSize;
            if (_bufferSize < 1 || _bufferSize > DuetAPI.Connection.Defaults.MaxCodeBufferSize)
            {
                throw new ArgumentException("BufferSize is out of range");
            }
            _channel = codeStreamInitMessage.Channel;
            _logger.Debug("CodeStream processor added for IPC#{0}", conn.Id);
        }

        /// <summary>
        /// Reads incoming codes and processes them asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Process()
        {
            await using NetworkStream stream = new(Connection.UnixSocket);
            using StreamReader streamReader = new(stream);
            await using StreamWriter streamWriter = new(stream);

            _streamWriter = streamWriter;
            lock (_streams)
            {
                _streams.Add(this);
            }

            try
            {
                CodeParserBuffer parserBuffer = new(Settings.FileBufferSize, false);
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
                    parserBuffer.MayRepeatCode = Model.Provider.Get.State.MachineMode == MachineMode.CNC ||
                                                 Model.Provider.Get.State.MachineMode == MachineMode.Laser;
                }

                // Prepare some code instances as a buffer
                int numCodes = Math.Max(_bufferSize, 1);
                AsyncMonitor codeLock = new();
                Queue<Code> codes = new();
                for (int i = 0; i < numCodes; i++)
                {
                    codes.Enqueue(new Code());
                }

                do
                {
                    try
                    {
                        // Read the next line from the client
                        string line = await streamReader.ReadLineAsync();
                        if (line == null)
                        {
                            break;
                        }

                        // Attempt to parse it. Throw it away if a parse error occurs
                        await using MemoryStream lineStream = new(Encoding.UTF8.GetBytes(line));
                        using StreamReader lineReader = new(lineStream);

                        do
                        {
                            // Get another code instance
                            Code code;
                            using (await codeLock.EnterAsync(Program.CancellationToken))
                            {
                                if (!codes.TryDequeue(out code))
                                {
                                    await codeLock.WaitAsync(Program.CancellationToken);
                                    code = codes.Dequeue();
                                }
                            }
                            code.Reset();

                            // Read the next code from the stream, execute it, and put the code instance back into the buffer
                            try
                            {
                                if (await DuetAPI.Commands.Code.ParseAsync(lineReader, code, parserBuffer))
                                {
                                    code.Channel = _channel;
                                    code.Connection = Connection;
                                    code.SourceConnection = Connection.Id;
                                    _ = code
                                        .Execute()
                                        .ContinueWith(async task =>
                                        {
                                            try
                                            {
                                                Message result = await task;
                                                using (await _outputLock.LockAsync(Program.CancellationToken))
                                                {
                                                    await streamWriter.WriteAsync(result.ToString());
                                                    await streamWriter.FlushAsync();
                                                }
                                            }
                                            catch (CodeParserException cpe)
                                            {
                                                await streamWriter.WriteLineAsync("Error: " + cpe.Message);
                                                using (await Model.Provider.AccessReadOnlyAsync())
                                                {
                                                    // Repetier or other host servers expect an "ok" after error messages
                                                    if (Model.Provider.Get.Inputs[_channel].Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                                                    {
                                                        await streamWriter.WriteLineAsync("ok");
                                                    }
                                                }
                                                await streamWriter.FlushAsync();
                                            }
                                            catch (SocketException)
                                            {
                                                // Connection has been terminated
                                            }
                                            finally
                                            {
                                                using (await codeLock.EnterAsync(Program.CancellationToken))
                                                {
                                                    codes.Enqueue(code);
                                                    codeLock.Pulse();
                                                }
                                            }
                                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                                }
                                else
                                {
                                    // No more codes available, put back the reserved code
                                    using (await codeLock.EnterAsync(Program.CancellationToken))
                                    {
                                        codes.Enqueue(code);
                                        codeLock.Pulse();
                                    }
                                    break;
                                }
                            }
                            catch (CodeParserException cpe)
                            {
                                parserBuffer.Invalidate();
                                _logger.Warn(cpe, "IPC#{0}: Failed to parse code from code stream", Connection.Id);

                                using (await codeLock.EnterAsync(Program.CancellationToken))
                                {
                                    // Put this faulty code back into the queue and wait for all other pending codes to finish.
                                    // Flushing the code channel only does not work here because the code reply has to be written as well
                                    codes.Enqueue(code);
                                    while (codes.Count < numCodes)
                                    {
                                        await codeLock.WaitAsync(Program.CancellationToken);
                                    }
                                }

                                await streamWriter.WriteLineAsync($"Error: Failed to parse code from line '{line}'");
                                using (await Model.Provider.AccessReadOnlyAsync())
                                {
                                    if (Model.Provider.Get.Inputs[_channel].Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                                    {
                                        await streamWriter.WriteLineAsync("ok");
                                    }
                                }
                                await streamWriter.FlushAsync();
                                break;
                            }
                        } while (!Program.CancellationToken.IsCancellationRequested);

                        // Shut down the socket if this was the last command
                        if (Program.CancellationToken.IsCancellationRequested)
                        {
                            Connection.Close();
                        }
                    }
                    catch (SocketException)
                    {
                        // Connection has been terminated
                        break;
                    }
                    catch (Exception e)
                    {
                        // Send errors back to the client
                        if (e is not OperationCanceledException)
                        {
                            _logger.Error(e, "IPC#{0}: Failed to execute stream code", Connection.Id);
                        }
                        await Connection.SendResponse(e);
                    }
                }
                while (!Program.CancellationToken.IsCancellationRequested);
            }
            finally
            {
                lock (_streams)
                {
                    _streams.Remove(this);
                }
                _streamWriter = null;
            }
        }

        /// <summary>
        /// Record a new message based on the message flags
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="message"></param>
        public static void RecordMessage(MessageTypeFlags flags, Message message)
        {
            lock (_streams)
            {
                foreach (CodeStream stream in _streams)
                {
                    MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)stream._channel);
                    if (flags.HasFlag(channelFlag))
                    {
                        stream.RecordMessage(message);
                    }
                }
            }
        }

        /// <summary>
        /// Record a new message
        /// </summary>
        /// <param name="message"></param>
        private void RecordMessage(Message message)
        {
            _ = _outputLock
                .LockAsync(Program.CancellationToken)
                .AsTask()
                .ContinueWith(async task =>
                {
                    using (await task)
                    {
                        await _streamWriter?.WriteAsync(message.ToString());
                        await _streamWriter?.FlushAsync();
                    }
                }, TaskContinuationOptions.RunContinuationsAsynchronously);
        }
    }
}
