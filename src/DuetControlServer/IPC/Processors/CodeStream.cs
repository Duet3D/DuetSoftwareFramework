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

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Command interpreter for code streams
    /// </summary>
    public sealed class CodeStream : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Code)
        };

        /// <summary>
        /// Maximum number of codes to execute simultaneously
        /// </summary>
        private readonly int _bufferSize;

        /// <summary>
        /// Code channel for incoming codes
        /// </summary>
        private readonly CodeChannel _channel;

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
            conn.Logger.Debug("CodeStream processor added");
        }

        /// <summary>
        /// Reads incoming codes and processes them asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Process()
        {
            using NetworkStream stream = new(Connection.UnixSocket);
            using StreamReader streamReader = new(stream);
            using StreamWriter streamWriter = new(stream);

            CodeParserBuffer parserBuffer = new(Settings.FileBufferSize, false);
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
                parserBuffer.MayRepeatCode = Model.Provider.Get.State.MachineMode == DuetAPI.ObjectModel.MachineMode.CNC ||
                                             Model.Provider.Get.State.MachineMode == DuetAPI.ObjectModel.MachineMode.Laser;
            }

            AsyncMonitor codeLock = new();
            Queue<Code> codes = new();
            for (int i = 0; i < Math.Max(_bufferSize, 1); i++)
            {
                codes.Enqueue(new Code());
            }
            AsyncLock outputLock = new();

            do
            {
                try
                {
                    // Read the next line
                    string line = await streamReader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }
                    using MemoryStream lineStream = new(Encoding.UTF8.GetBytes(line));
                    using StreamReader lineReader = new(lineStream);

                    do
                    {
                        // Get another code instance from the line
                        Code code;
                        using (await codeLock.EnterAsync(Program.CancellationToken))
                        {
                            if (!codes.TryDequeue(out code))
                            {
                                await codeLock.WaitAsync(Program.CancellationToken);
                                code = codes.Dequeue();
                            }
                        }

                        // Read the next code from the stream, execute it, and put the code instance back into the buffer
                        code.Reset();
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
                                        using (await outputLock.LockAsync(Program.CancellationToken))
                                        {
                                            CodeResult result = await task;
                                            await streamWriter.WriteLineAsync(result.ToString().TrimEnd());
                                            await streamWriter.FlushAsync();
                                        }
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
                    if (!(e is OperationCanceledException))
                    {
                        Connection.Logger.Error(e, "Failed to execute code");
                    }
                    await Connection.SendResponse(e);
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }
    }
}
