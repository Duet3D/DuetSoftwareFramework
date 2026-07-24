using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace DuetAPI.Utility;

/// <summary>
/// Helper class for JSON serialization, deserialization, patch creation and patch application
/// </summary>
public static class JsonHelper
{
    /// <summary>
    /// Resolver for the source-generated metadata of the object model, command and connection contexts
    /// </summary>
    /// <remarks>
    /// The contexts are generated with the same naming policy and object creation handling as the options
    /// below, so routing a type through them yields the same JSON. There is deliberately no reflection-based
    /// fallback: a type that no context covers must fail loudly rather than depend on reflection at runtime.
    /// This field is declared first because static initializers run in textual order
    /// </remarks>
    private static readonly IJsonTypeInfoResolver _typeInfoResolver = JsonTypeInfoResolver.Combine(ObjectModel.ObjectModelContext.Default, Commands.CommandContext.Default, Connection.ConnectionContext.Default, CommonContext.Default);

    /// <summary>
    /// Default JSON (de-)serialization options
    /// </summary>
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = _typeInfoResolver
    };

    /// <summary>
    /// JSON (de-)serialization options that omit null values
    /// </summary>
    public static readonly JsonSerializerOptions NoNullJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = _typeInfoResolver
    };

#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
    /// <summary>
    /// Size of the read chunks rented by <see cref="ReceiveUtf8Json"/> and its async variants (in bytes)
    /// </summary>
    public static int ReceiveBufferSize { get; set; } = 8192;

    /// <summary>
    /// Receive a serialized JSON object from a socket in UTF-8 format
    /// </summary>
    /// <param name="socket">Socket to read from</param>
    /// <returns>Plain JSON</returns>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public static MemoryStream ReceiveUtf8Json(Socket socket)
    {
        MemoryStream jsonStream = new();
        bool inJson = false, inQuotes = false, isEscaped = false;
        int numBraces = 0;

        // The protocol has no length framing, so the end of a JSON object can only be found by scanning it.
        // Peek whole chunks and consume only the bytes belonging to the current object to avoid one syscall per byte
        byte[] readData = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (!inJson || numBraces > 0)
            {
                int bytesPeeked = socket.Receive(readData, 0, readData.Length, SocketFlags.Peek);
                if (bytesPeeked <= 0)
                {
                    // Do not keep reading if the connection has been gracefully closed
                    jsonStream.Dispose();
                    throw new SocketException((int)SocketError.NotConnected);
                }

                int jsonStart = inJson ? 0 : -1, scanned = 0;
                while (scanned < bytesPeeked && (!inJson || numBraces > 0))
                {
                    char c = (char)readData[scanned];
                    if (inQuotes)
                    {
                        if (isEscaped)
                        {
                            isEscaped = false;
                        }
                        else if (c == '\\')
                        {
                            isEscaped = true;
                        }
                        else if (c == '"')
                        {
                            inQuotes = false;
                        }
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == '{')
                    {
                        if (!inJson)
                        {
                            inJson = true;
                            jsonStart = scanned;
                        }
                        numBraces++;
                    }
                    else if (c == '}')
                    {
                        numBraces--;
                    }
                    scanned++;
                }

                if (jsonStart >= 0)
                {
                    jsonStream.Write(readData, jsonStart, scanned - jsonStart);
                }

                for (int consumed = 0; consumed < scanned; )
                {
                    int bytesRead = socket.Receive(readData, 0, scanned - consumed, SocketFlags.None);
                    if (bytesRead <= 0)
                    {
                        jsonStream.Dispose();
                        throw new SocketException((int)SocketError.NotConnected);
                    }
                    consumed += bytesRead;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readData);
        }

        jsonStream.Seek(0, SeekOrigin.Begin);
        return jsonStream;
    }

    /// <summary>
    /// Receive a serialized JSON object from a socket in UTF-8 format asynchronously
    /// </summary>
    /// <param name="socket">Socket to read from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Plain JSON</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public static async ValueTask<MemoryStream> ReceiveUtf8JsonAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        MemoryStream jsonStream = new();
        try
        {
            await ReceiveUtf8JsonAsync(socket, jsonStream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            jsonStream.Dispose();
            throw;
        }

        jsonStream.Seek(0, SeekOrigin.Begin);
        return jsonStream;
    }

    /// <summary>
    /// Receive a serialized JSON object from a socket in UTF-8 format asynchronously, appending it to a given stream.
    /// Useful to avoid allocating a new buffer per message on hot paths
    /// </summary>
    /// <param name="socket">Socket to read from</param>
    /// <param name="jsonStream">Stream to append the received JSON to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Connection has been closed</exception>
    public static async ValueTask ReceiveUtf8JsonAsync(Socket socket, MemoryStream jsonStream, CancellationToken cancellationToken = default)
    {
        bool inJson = false, inQuotes = false, isEscaped = false;
        int numBraces = 0;

        // The protocol has no length framing, so the end of a JSON object can only be found by scanning it.
        // Peek whole chunks and consume only the bytes belonging to the current object to avoid one syscall per byte
        byte[] readData = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (!inJson || numBraces > 0)
            {
                int bytesPeeked = await socket.ReceiveAsync(readData.AsMemory(), SocketFlags.Peek, cancellationToken).ConfigureAwait(false);
                if (bytesPeeked <= 0)
                {
                    // Do not keep reading if the connection has been gracefully closed
                    throw new SocketException((int)SocketError.NotConnected);
                }

                int jsonStart = inJson ? 0 : -1, scanned = 0;
                while (scanned < bytesPeeked && (!inJson || numBraces > 0))
                {
                    char c = (char)readData[scanned];
                    if (inQuotes)
                    {
                        if (isEscaped)
                        {
                            isEscaped = false;
                        }
                        else if (c == '\\')
                        {
                            isEscaped = true;
                        }
                        else if (c == '"')
                        {
                            inQuotes = false;
                        }
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == '{')
                    {
                        if (!inJson)
                        {
                            inJson = true;
                            jsonStart = scanned;
                        }
                        numBraces++;
                    }
                    else if (c == '}')
                    {
                        numBraces--;
                    }
                    scanned++;
                }

                if (jsonStart >= 0)
                {
                    jsonStream.Write(readData, jsonStart, scanned - jsonStart);
                }

                for (int consumed = 0; consumed < scanned; )
                {
                    int bytesRead = await socket.ReceiveAsync(readData.AsMemory(0, scanned - consumed), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        throw new SocketException((int)SocketError.NotConnected);
                    }
                    consumed += bytesRead;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readData);
        }
    }
#endif
}
