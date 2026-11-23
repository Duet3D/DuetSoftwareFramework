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
using AsyncLock = DuetSharedLibrary.AsyncLock;
using Code = DuetControlServer.Commands.Code;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging;

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
    public static SupportedCommand[] SupportedCommands { get; } =
    [
        SupportedCommand.For<Code>()
    ];

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
    /// Lock for outputting data
    /// </summary>
    private readonly AsyncLock _outputLock = new();

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    // Stream I/O
    private readonly int _bufferSize;
    private readonly CodeChannel _channel;
    private readonly NetworkStream _stream;
    private readonly StreamWriter _streamWriter;

    // Raw input, because the data of a binary upload follows its code as raw bytes
    private readonly byte[] _readBuffer;
    private int _readStart, _readEnd;
    private bool _skipLineFeed;

    // Private fields
    private readonly Codes.CodeFactory _codeFactory;
    private readonly Codes.CodeProcessor _codeProcessor;
    private readonly Utility.EventLogger _eventLogger;
    private readonly Codes.Meta.Expressions _expressions;
    private readonly Files.FilePathResolver _filePathResolver;
    private readonly Model.ObjectModel _model;
    private readonly ILogger _logger;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of the code stream interpreter
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message from the client</param>
    /// <param name="codeFactory">Code factory to create code instances</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="eventLogger">Event logger</param>
    /// <param name="expressions">Expression evaluator</param>
    /// <param name="filePathResolver">Resolver for virtual file paths</param>
    /// <param name="model">Object model</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    public CodeStream(Connection conn, ClientInitMessage initMessage, Codes.CodeFactory codeFactory, Codes.CodeProcessor codeProcessor, Utility.EventLogger eventLogger, Codes.Meta.Expressions expressions, Files.FilePathResolver filePathResolver, Model.ObjectModel model, ILogger<CodeStream> logger, IOptions<Settings> settings)
    {
        Connection = conn;
        _stream = new NetworkStream(conn.UnixSocket);
        _streamWriter = new(_stream);
        _readBuffer = new byte[settings.Value.FileBufferSize];
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
        _codeProcessor = codeProcessor;
        _eventLogger = eventLogger;
        _expressions = expressions;
        _filePathResolver = filePathResolver;
        _model = model;
        _logger = logger;
        _settings = settings.Value;

        logger.LogDebug("CodeStream processor added for IPC#{Id}", conn.Id);
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
            // Fanuc CNC and LaserWeb G-code may omit the last major G-code number
            parserBuffer.MayRepeatCode = _model.CurrentMachineMode is MachineMode.CNC or MachineMode.Laser;

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
                    string? line = await ReadLineAsync(cancellationToken);
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

                                if (code.Type == CodeType.MCode && code.MajorNumber is 559 or 560)
                                {
                                    // The file data follows this line as raw bytes, so it must be taken from the stream before another line is read.
                                    // Codes are executed asynchronously, so wait for the pending ones first to keep their replies in order
                                    using (await codeLock.EnterAsync(cancellationToken))
                                    {
                                        while (codes.Count < numCodes - 1)
                                        {
                                            await codeLock.WaitAsync(cancellationToken);
                                        }
                                    }

                                    // While M28 is writing a file, this code is a line of that file like any other
                                    bool writingTextFile;
                                    using (await _codeProcessor.FileLocks[(int)_channel].LockAsync(cancellationToken))
                                    {
                                        writingTextFile = _codeProcessor.FilesBeingWritten[(int)_channel] is not null;
                                    }
                                    if (!writingTextFile)
                                    {
                                        try
                                        {
                                            await ReceiveFileAsync(code, cancellationToken);
                                        }
                                        finally
                                        {
                                            using (await codeLock.EnterAsync(CancellationToken.None))
                                            {
                                                codes.Enqueue(code);
                                                codeLock.Pulse();
                                            }
                                        }
                                        continue;
                                    }
                                }

                                _ = code
                                    .ExecuteAsync()
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
                                        catch (Exception e) when (e is CodeParserException or ArgumentException)
                                        {
                                            await _streamWriter.WriteLineAsync("Error: " + e.Message);
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
                            _logger.LogWarning(cpe, "IPC#{Id}: Failed to parse code from code stream", Connection.Id);

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
                        _logger.LogError(e, "IPC#{Id}: Failed to execute stream code", Connection.Id);
                    }
                    await Connection.SendExceptionAsync(e);
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
    /// Read more data from the client into the empty read buffer
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>False if the client has closed the connection</returns>
    private async ValueTask<bool> FillReadBufferAsync(CancellationToken cancellationToken)
    {
        // Both positions change only once the read has succeeded, else a failed read would make the old content look unread
        int bytesRead = await _stream.ReadAsync(_readBuffer, cancellationToken);
        _readStart = 0;
        _readEnd = bytesRead;
        return bytesRead > 0;
    }

    /// <summary>
    /// Skip the line feed of a CR LF line ending whose carriage return ended the previous line
    /// </summary>
    private void SkipPendingLineFeed()
    {
        if (_skipLineFeed)
        {
            _skipLineFeed = false;
            if (_readBuffer[_readStart] == (byte)'\n')
            {
                _readStart++;
            }
        }
    }

    /// <summary>
    /// Read the next line from the client. Lines may end with LF, CR or CR LF
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Line without its line ending or null if the client has closed the connection</returns>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using MemoryStream line = new();
        while (true)
        {
            if (_readStart == _readEnd)
            {
                if (!await FillReadBufferAsync(cancellationToken))
                {
                    return (line.Length != 0) ? Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length) : null;
                }
                SkipPendingLineFeed();
                continue;
            }

            ReadOnlySpan<byte> available = _readBuffer.AsSpan(_readStart, _readEnd - _readStart);
            int lineEnd = available.IndexOfAny((byte)'\r', (byte)'\n');
            if (lineEnd < 0)
            {
                line.Write(available);
                _readStart = _readEnd;
            }
            else
            {
                line.Write(available[..lineEnd]);
                _readStart += lineEnd + 1;
                if (available[lineEnd] == (byte)'\r')
                {
                    // The line feed of a CR LF ending is taken at once if it is here already, else when more data has arrived
                    if (lineEnd + 1 == available.Length)
                    {
                        _skipLineFeed = true;
                    }
                    else if (available[lineEnd + 1] == (byte)'\n')
                    {
                        _readStart++;
                    }
                }
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }
        }
    }

    /// <summary>
    /// Copy raw bytes from the client to a stream.
    /// If writing fails, the remaining bytes are still taken from the client and discarded, because they would be read as codes otherwise
    /// </summary>
    /// <param name="destination">Stream to write to</param>
    /// <param name="count">Number of bytes to copy</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>False if the client has closed the connection before all bytes were received, and the first write error</returns>
    private async Task<(bool, Exception?)> CopyBytesAsync(Stream destination, long count, CancellationToken cancellationToken)
    {
        // A line feed still awaited after a carriage return counts as data, because a client may end its lines with CR only
        _skipLineFeed = false;

        long received = 0;
        Exception? writeError = null;
        while (received < count)
        {
            if (_readStart == _readEnd && !await FillReadBufferAsync(cancellationToken))
            {
                return (false, writeError);
            }

            int bytesToCopy = (int)Math.Min(count - received, _readEnd - _readStart);
            if (writeError is null)
            {
                try
                {
                    await destination.WriteAsync(_readBuffer.AsMemory(_readStart, bytesToCopy), cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Any failure, not only an I/O error: a file that grows too large throws ArgumentOutOfRangeException
                    writeError = e;
                }
            }
            _readStart += bytesToCopy;
            received += bytesToCopy;
        }
        return (true, writeError);
    }

    /// <summary>
    /// String that ends the data of a binary upload without a file size, same as in RepRapFirmware
    /// </summary>
    private static ReadOnlySpan<byte> EndOfFileString => "<!-- **EoF** -->"u8;

    /// <summary>
    /// Copy raw bytes from the client to a stream until the end-of-file string is received, which is not copied.
    /// If writing fails, the remaining bytes are still taken from the client and discarded
    /// </summary>
    /// <param name="destination">Stream to write to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>False if the client has closed the connection before the end-of-file string was received, and the first write error</returns>
    private async Task<(bool, Exception?)> CopyUntilEndOfFileStringAsync(Stream destination, CancellationToken cancellationToken)
    {
        _skipLineFeed = false;

        // The first character of the end-of-file string occurs nowhere else in it, so a failed match can only restart at the current byte
        byte[] output = new byte[_readBuffer.Length + EndOfFileString.Length];
        int matched = 0;
        Exception? writeError = null;
        while (true)
        {
            if (_readStart == _readEnd && !await FillReadBufferAsync(cancellationToken))
            {
                return (false, writeError);
            }

            int outputLength = 0;
            while (_readStart < _readEnd && matched < EndOfFileString.Length)
            {
                byte b = _readBuffer[_readStart++];
                if (b != EndOfFileString[matched])
                {
                    EndOfFileString[..matched].CopyTo(output.AsSpan(outputLength));
                    outputLength += matched;
                    matched = 0;
                }
                if (b == EndOfFileString[matched])
                {
                    matched++;
                }
                else
                {
                    output[outputLength++] = b;
                }
            }

            if (writeError is null)
            {
                try
                {
                    await destination.WriteAsync(output.AsMemory(0, outputLength), cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Any failure, not only an I/O error: a file that grows too large throws ArgumentOutOfRangeException
                    writeError = e;
                }
            }
            if (matched == EndOfFileString.Length)
            {
                return (true, writeError);
            }
        }
    }

    /// <summary>
    /// Write a reply that does not come from an executed code
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="onlyIfEmulatingMarlin">Send an empty reply unless the channel emulates Marlin</param>
    /// <returns>Asynchronous task</returns>
    private async Task WriteReplyAsync(Message message, CancellationToken cancellationToken, bool onlyIfEmulatingMarlin = false)
    {
        bool emulatingMarlin = _model.IsEmulatingMarlin(_channel);
        if (onlyIfEmulatingMarlin && !emulatingMarlin)
        {
            message = new Message();
        }
        await _eventLogger.LogAsync(message);

        // An empty reply is sent as an empty line, like the reply of an executed code
        string reply = message.ToString();
        if (!reply.EndsWith('\n'))
        {
            reply += '\n';
        }
        if (emulatingMarlin)
        {
            reply += "ok\n";
        }

        using (await _outputLock.LockAsync(cancellationToken))
        {
            await _streamWriter.WriteAsync(reply);
            await _streamWriter.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Receive a binary file upload started by M559 or M560.
    /// The file data follows the code as raw bytes and must not be sent before the "Writing to file" reply has been received, like on a serial input of the main board.
    /// It ends after the number of bytes given by the S parameter, or at the end-of-file string if there is none.
    /// The data is written to a temporary file, which replaces the target only when the upload is complete and its checksum is correct.
    /// </summary>
    /// <param name="code">M559 or M560 code that starts the upload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task ReceiveFileAsync(Code code, CancellationToken cancellationToken)
    {
        // Nothing may be taken from the stream unless "Writing to file" has been sent.
        // Once it has been sent, all the file data must be taken whatever happens, else the client and this end disagree about what is a code
        string prefix = $"M{code.MajorNumber}: ", file, physicalFile;
        long size;
        uint expectedCrc;
        try
        {
            await _expressions.EvaluateAsync(code, true, cancellationToken);
            file = code.GetString('P');
            size = code.GetLong('S', 0);
            expectedCrc = code.GetUInt('C', 0);
            // Sizes beyond 32 bits are not parsed exactly and RepRapFirmware does not take them either
            if (size < 0 || size > uint.MaxValue)
            {
                throw new ArgumentException("the file size (S parameter) is out of range");
            }
            physicalFile = await _filePathResolver.ToPhysicalAsync(file, (code.MajorNumber == 560) ? FileDirectory.Web : FileDirectory.System, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Failed to start file upload");
            await WriteReplyAsync(new Message(MessageType.Error, $"{prefix}{e.Message}"), cancellationToken);
            return;
        }

        string partFile = physicalFile + ".part";
        FileStream fileStream;
        try
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileName(physicalFile)) || Directory.Exists(physicalFile))
            {
                throw new IOException("Target is not a file");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(physicalFile)!);
            fileStream = new(partFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, _settings.FileBufferSize);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Failed to open file for writing");
            await WriteReplyAsync(new Message(MessageType.Error, $"{prefix}Can't open file {file} for writing"), cancellationToken);
            return;
        }

        // The temporary file is moved or deleted while it is still open, so that another upload to the same target cannot open it in between
        bool moved = false;
        try
        {
            await WriteReplyAsync(new Message(MessageType.Success, $"Writing to file: {file}"), cancellationToken);
            (bool received, Exception? writeError) = (size != 0) ? await CopyBytesAsync(fileStream, size, cancellationToken) : await CopyUntilEndOfFileStringAsync(fileStream, cancellationToken);
            if (!received)
            {
                await _eventLogger.LogOutputAsync(MessageType.Error, $"Upload of {file} aborted because the client disconnected, partial file deleted", cancellationToken);
                return;
            }

            string? error = null;
            if (writeError is not null)
            {
                error = $"Can't write file {file}: {writeError.Message}";
            }
            else
            {
                try
                {
                    fileStream.Flush(true);
                    if (expectedCrc != 0)
                    {
                        fileStream.Position = 0;
                        if (await Utility.CRC32.CalculateAsync(fileStream, _settings.FileBufferSize, cancellationToken) != expectedCrc)
                        {
                            error = "CRC32 checksum doesn't match";
                        }
                    }
                    if (error is null)
                    {
                        File.Move(partFile, physicalFile, true);
                        moved = true;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning(e, "IPC#{Id}: Failed to write uploaded file {File}", Connection.Id, file);
                    error = $"Can't write file {file}: {e.Message}";
                }
            }

            if (error is not null)
            {
                await WriteReplyAsync(new Message(MessageType.Error, $"{prefix}{error}"), cancellationToken);
            }
            else
            {
                await WriteReplyAsync(new Message(MessageType.Success, "Done saving file."), cancellationToken, onlyIfEmulatingMarlin: true);
            }
        }
        finally
        {
            // Neither step may keep the other from running or replace an exception that is already on its way
            if (!moved)
            {
                try
                {
                    File.Delete(partFile);
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Failed to delete partial file");
                }
            }
            try
            {
                await fileStream.DisposeAsync();
            }
            catch (Exception e)
            {
                // Closing flushes once more, which fails again if writing failed before
                _logger.LogDebug(e, "Failed to close partial file");
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
                try
                {
                    using (await task)
                    {
                        await _streamWriter!.WriteAsync(message.ToString());
                        await _streamWriter!.FlushAsync();
                    }
                }
                catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
                {
                    // Client is gone or the stream has been disposed concurrently
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
        _stream.Dispose();
        _disposed = true;
    }
}
