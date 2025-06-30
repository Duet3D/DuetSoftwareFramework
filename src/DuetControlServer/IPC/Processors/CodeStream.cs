using DuetAPI.Commands;
using DuetAPI;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Command interpreter for code streams
/// </summary>
public sealed class CodeStream : IProcessor, IDisposable
{
    /// <summary>
    /// List of supported commands in this mode.
    /// This is not really used because this mode reads lines and no JSON objects
    /// </summary>
    public static Type[] SupportedCommands { get; } =
    [
        typeof(Code)
    ];

    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// List of active subscribers
    /// </summary>
    private static readonly List<CodeStream> _streams = [];

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
    /// Stream for communication with a client
    /// </summary>
    private readonly NetworkStream _stream;

    /// <summary>
    /// Stream reader for reading from a client
    /// </summary>
    private readonly StreamReader _streamReader;

    /// <summary>
    /// Stream for writing to a client
    /// </summary>
    private readonly StreamWriter _streamWriter;

    /// <summary>
    /// Code factory
    /// </summary>
    private readonly Codes.CodeFactory _codeFactory;

    /// <summary>
    /// Object model
    /// </summary>
    private readonly Model.ObjectModel _model;

    /// <summary>
    /// Settings
    /// </summary>
    private readonly Settings _settings;

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Constructor of the code stream interpreter
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    public CodeStream(Connection conn, ClientInitMessage initMessage, Codes.CodeFactory codeFactory, Model.ObjectModel model, IOptions<Settings> settings)
    {
        Connection = conn;
        _stream = new NetworkStream(conn.UnixSocket);
        _streamReader = new(_stream);
        _streamWriter = new(_stream);
        lock (_streams)
        {
            _streams.Add(this);
        }

        CodeStreamInitMessage codeStreamInitMessage = (CodeStreamInitMessage)initMessage;
        _bufferSize = codeStreamInitMessage.BufferSize;
        if (_bufferSize < 1 || _bufferSize > DuetAPI.Connection.Defaults.MaxCodeBufferSize)
        {
            throw new ArgumentException("BufferSize is out of range");
        }
        _channel = codeStreamInitMessage.Channel;

        _codeFactory = codeFactory;
        _model = model;
        _settings = settings.Value;

        _logger.Debug("CodeStream processor added for IPC#{0}", conn.Id);
    }

    /// <summary>
    /// Reads incoming codes and processes them asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            CodeParserBuffer parserBuffer = new(_settings.FileBufferSize, false);
            using (await _model.AccessReadOnlyAsync(cancellationToken))
            {
                // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
                parserBuffer.MayRepeatCode = _model.State.MachineMode is MachineMode.CNC or MachineMode.Laser;
            }

            // Prepare some code instances as a buffer
            int numCodes = Math.Max(_bufferSize, 1);
            AsyncMonitor codeLock = new();
            Queue<Code> codes = new();
            for (int i = 0; i < numCodes; i++)
            {
                codes.Enqueue(_codeFactory.Create());
            }

            do
            {
                try
                {
                    // Read the next line from the client
                    string? line = await _streamReader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    // Attempt to parse it. Throw it away if a parse error occurs
                    await using MemoryStream lineStream = new(Encoding.UTF8.GetBytes(line));

                    do
                    {
                        // Get another code instance
                        Code? code;
                        using (await codeLock.EnterAsync(cancellationToken))
                        {
                            if (!codes.TryDequeue(out code))
                            {
                                await codeLock.WaitAsync(cancellationToken);
                                code = codes.Dequeue();
                            }
                        }
                        code.Reset();

                        // Read the next code from the stream, execute it, and put the code instance back into the buffer
                        try
                        {
                            if (await DuetAPI.Commands.Code.ParseAsync(lineStream, code, parserBuffer, cancellationToken))
                            {
                                code.Channel = _channel;
                                code.Connection = Connection;
                                code.SourceConnection = Connection.Id;
                                _ = code
                                    .ExecuteAsync(cancellationToken)
                                    .ContinueWith(async task =>
                                    {
                                        try
                                        {
                                            Message? result = await task;
                                            if (result is not null)
                                            {
                                                using (await _outputLock.LockAsync(cancellationToken))
                                                {
                                                    await _streamWriter.WriteAsync(result.ToString());
                                                    await _streamWriter.FlushAsync();
                                                }
                                            }
                                        }
                                        catch (CodeParserException cpe)
                                        {
                                            await _streamWriter.WriteLineAsync("Error: " + cpe.Message);
                                            using (await _model.AccessReadOnlyAsync())
                                            {
                                                // Repetier or other host servers expect an "ok" after error messages
                                                if (_model.Inputs[_channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                                                {
                                                    await _streamWriter.WriteLineAsync("ok");
                                                }
                                            }
                                            await _streamWriter.FlushAsync();
                                        }
                                        catch (SocketException)
                                        {
                                            // Connection has been terminated
                                        }
                                        finally
                                        {
                                            using (await codeLock.EnterAsync(cancellationToken))
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
                                using (await codeLock.EnterAsync(cancellationToken))
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

                            using (await codeLock.EnterAsync(cancellationToken))
                            {
                                // Put this faulty code back into the queue and wait for all other pending codes to finish.
                                // Flushing the code channel only does not work here because the code reply has to be written as well
                                codes.Enqueue(code);
                                while (codes.Count < numCodes)
                                {
                                    await codeLock.WaitAsync(cancellationToken);
                                }
                            }

                            await _streamWriter.WriteLineAsync($"Error: Failed to parse code from line '{line}'");
                            using (await _model.AccessReadOnlyAsync(cancellationToken))
                            {
                                if (_model.Inputs[_channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                                {
                                    await _streamWriter.WriteLineAsync("ok");
                                }
                            }
                            await _streamWriter.FlushAsync(cancellationToken);
                            break;
                        }
                    } while (!cancellationToken.IsCancellationRequested);

                    // Shut down the socket if this was the last command
                    if (cancellationToken.IsCancellationRequested)
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
            while (!cancellationToken.IsCancellationRequested);
        }
        finally
        {
            lock (_streams)
            {
                _streams.Remove(this);
            }
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
    /// <param name="message">Message to record</param>
    private void RecordMessage(Message message)
    {
        _ = _outputLock
            .LockAsync()
            .AsTask()
            .ContinueWith(async task =>
            {
                using (await task)
                {
                    await _streamWriter!.WriteAsync(message.ToString());
                    await _streamWriter!.FlushAsync();
                }
            }, TaskContinuationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Flag indicating whether the code stream interpreter has been disposed
    /// </summary>
    private bool _disposed = false;

    /// <summary>
    /// Dispose of the code stream interpreter
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _streamWriter.Dispose();
        _streamReader.Dispose();
        _stream.Dispose();
        _disposed = true;
    }
}
