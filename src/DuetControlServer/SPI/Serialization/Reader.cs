using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using DuetAPI;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;

namespace DuetControlServer.SPI.Serialization
{
    /// <summary>
    /// Static class for reading data from SPI transmissions.
    /// It is expected that each data block occupies entire 4-byte blocks.
    /// Make sure to keep the data returned by these functions only as long as the underlying buffer is actually valid!
    /// </summary>
    public static class Reader
    {
        /// <summary>
        /// Read a packet header from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packet">Read packet</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPacketHeader(ReadOnlySpan<byte> from, out PacketHeader packet)
        {
            packet = MemoryMarshal.Read<PacketHeader>(from);
            return Marshal.SizeOf<PacketHeader>();
        }

        /// <summary>
        /// Read a legacy config response from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="json">Config response JSON</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadLegacyConfigResponse(ReadOnlySpan<byte> from, out ReadOnlySpan<byte> json)
        {
            int jsonLength = MemoryMarshal.Read<ushort>(from);
            json = from.Slice(4, jsonLength);
            return 4 + jsonLength;
        }

        /// <summary>
        /// Read a code buffer update from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="bufferSpace">Buffer space</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeBufferUpdate(ReadOnlySpan<byte> from, out ushort bufferSpace)
        {
            CodeBufferUpdateHeader header = MemoryMarshal.Read<CodeBufferUpdateHeader>(from);
            bufferSpace = header.BufferSpace;
            return Marshal.SizeOf<CodeBufferUpdateHeader>();
        }

        /// <summary>
        /// Read a message from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="messageType">Message flags</param>
        /// <param name="reply">Raw message</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMessage(ReadOnlySpan<byte> from, out MessageTypeFlags messageType, out string reply)
        {
            MessageHeader header = MemoryMarshal.Read<MessageHeader>(from);
            int bytesRead = Marshal.SizeOf<MessageHeader>();

            // Read header
            messageType = header.MessageType;

            // Read message content
            if (header.Length > 0)
            {
                ReadOnlySpan<byte> unicodeReply = from.Slice(bytesRead, header.Length);
                reply = Encoding.UTF8.GetString(unicodeReply);
                bytesRead += header.Length;
            }
            else
            {
                reply = string.Empty;
            }
            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a macro file request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel that requested the execution</param>
        /// <param name="fromCode">Whether the macro request came from the G/M/T-code being executed</param>
        /// <param name="filename">Filename of the macro to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMacroRequest(ReadOnlySpan<byte> from, out CodeChannel channel, out bool fromCode, out string filename)
        {
            ExecuteMacroHeader header = MemoryMarshal.Read<ExecuteMacroHeader>(from);
            int bytesRead = Marshal.SizeOf<ExecuteMacroHeader>();
 
            // Read header
            channel = header.Channel;
            fromCode = Convert.ToBoolean(header.FromCode);

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, header.Length);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read information about an abort file request 
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel running the file</param>
        /// <param name="abortAll">Whether all files are supposed to be aborted</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadAbortFile(ReadOnlySpan<byte> from, out CodeChannel channel, out bool abortAll)
        {
            AbortFileHeader header = MemoryMarshal.Read<AbortFileHeader>(from);
            channel = (CodeChannel)header.Channel;
            abortAll = header.AbortAll != 0;
            return Marshal.SizeOf<AbortFileHeader>();
        }

        /// <summary>
        /// Read a print pause event
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filePosition">Position at which the print has been paused</param>
        /// <param name="reason">Reason why the print has been paused</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPrintPaused(ReadOnlySpan<byte> from, out uint filePosition, out PrintPausedReason reason)
        {
            PrintPausedHeader header = MemoryMarshal.Read<PrintPausedHeader>(from);
            filePosition = header.FilePosition;
            reason = (PrintPausedReason)header.PauseReason;
            return Marshal.SizeOf<PrintPausedHeader>();
        }

        /// <summary>
        /// Read a G-code channel
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Channel that has acquired the lock</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeChannel(ReadOnlySpan<byte> from, out CodeChannel channel)
        {
            CodeChannelHeader header = MemoryMarshal.Read<CodeChannelHeader>(from);
            channel = header.Channel;
            return Marshal.SizeOf<CodeChannelHeader>();
        }

        /// <summary>
        /// Read a file chunk request`
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filename">Filename to read from</param>
        /// <param name="offset">Offset in the file</param>
        /// <param name="maxLength">Maximum chunk length</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadFileChunkRequest(ReadOnlySpan<byte> from, out string filename, out uint offset, out int maxLength)
        {
            FileChunkHeader header = MemoryMarshal.Read<FileChunkHeader>(from);
            int bytesRead = Marshal.SizeOf<FileChunkHeader>();

            // Read header
            offset = header.Offset;
            maxLength = (int)header.MaxLength;

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, (int)header.FilenameLength);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += (int)header.FilenameLength;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a <see cref="Request.EvaluationResult"/> request
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="expression">Expression</param>
        /// <param name="result">Evaluation result</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadEvaluationResult(ReadOnlySpan<byte> from, out string expression, out object? result)
        {
            EvaluationResultHeader header = MemoryMarshal.Read<EvaluationResultHeader>(from);
            int bytesRead = Marshal.SizeOf<EvaluationResultHeader>();

            // Read expression
            ReadOnlySpan<byte> unicodeExpression = from.Slice(bytesRead, header.ExpressionLength);
            expression = Encoding.UTF8.GetString(unicodeExpression);
            bytesRead += header.ExpressionLength;

            // Read value
            switch (header.Type)
            {
                case DataType.Int:
                    result = header.IntValue;
                    break;
                case DataType.UInt:
                    result = header.UIntValue;
                    break;
                case DataType.Float:
                    result = header.FloatValue;
                    break;
                case DataType.ULong:
                    bytesRead = AddPadding(bytesRead);
                    result = MemoryMarshal.Read<ulong>(from[bytesRead..]);
                    break;
                case DataType.IntArray:
                    int[] intArray = new int[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        intArray[i] = MemoryMarshal.Read<int>(from[bytesRead..]);
                        bytesRead += sizeof(int);
                    }
                    result = intArray;
                    break;
                case DataType.UIntArray:
                    uint[] uintArray = new uint[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        uintArray[i] = MemoryMarshal.Read<uint>(from[bytesRead..]);
                        bytesRead += sizeof(uint);
                    }
                    result = uintArray;
                    break;
                case DataType.FloatArray:
                    float[] floatArray = new float[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        floatArray[i] = MemoryMarshal.Read<float>(from[bytesRead..]);
                        bytesRead += sizeof(float);
                    }
                    result = floatArray;
                    break;
                case DataType.String:
                    result = Encoding.UTF8.GetString(from.Slice(bytesRead, header.IntValue));
                    bytesRead += header.IntValue;
                    break;
                case DataType.DriverId:
                    result = new DriverId(header.UIntValue);
                    break;
                case DataType.DriverIdArray:
                    DriverId[] driverIdArray = new DriverId[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        driverIdArray[i] = new DriverId(MemoryMarshal.Read<uint>(from[bytesRead..]));
                        bytesRead += sizeof(uint);
                    }
                    result = driverIdArray;
                    break;
                case DataType.Bool:
                    result = Convert.ToBoolean(header.IntValue);
                    break;
                case DataType.BoolArray:
                    bool[] boolArray = new bool[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        boolArray[i] = Convert.ToBoolean(MemoryMarshal.Read<byte>(from[bytesRead..]));
                        bytesRead += sizeof(byte);
                    }
                    result = boolArray;
                    break;
                case DataType.Expression:
                    ReadOnlySpan<byte> expressionContent = from.Slice(bytesRead, header.IntValue);
                    result = null;
                    try
                    {
                        // Read an entire object from a JSON reader
                        Dictionary<string, object?> readObject(ref Utf8JsonReader reader)
                        {
                            Dictionary<string, object?> result = new();
                            string? propertyName = null;
                            while (reader.Read())
                            {
                                switch (reader.TokenType)
                                {
                                    case JsonTokenType.PropertyName:
                                        propertyName = reader.GetString();
                                        break;
                                    case JsonTokenType.StartObject:
                                        result.Add(propertyName!, readObject(ref reader));
                                        break;
                                    case JsonTokenType.EndObject:
                                        return result;
                                    case JsonTokenType.StartArray:
                                        result.Add(propertyName!, readArray(ref reader));
                                        break;
                                    case JsonTokenType.String:
                                        result.Add(propertyName!, reader.GetString());
                                        break;
                                    case JsonTokenType.Number:
                                        result.Add(propertyName!, reader.GetDouble());
                                        break;
                                    case JsonTokenType.True:
                                        result.Add(propertyName!, true);
                                        break;
                                    case JsonTokenType.False:
                                        result.Add(propertyName!, false);
                                        break;
                                    case JsonTokenType.Null:
                                        result.Add(propertyName!, null);
                                        break;
                                }
                            }
                            throw new JsonException();
                        }

                        // Read an entire array from a JSON reader
                        object?[] readArray(ref Utf8JsonReader reader)
                        {
                            List<object?> result = new();
                            while (reader.Read())
                            {
                                switch (reader.TokenType)
                                {
                                    case JsonTokenType.StartObject:
                                        result.Add(readObject(ref reader));
                                        break;
                                    case JsonTokenType.StartArray:
                                        result.Add(readArray(ref reader));
                                        break;
                                    case JsonTokenType.EndArray:
                                        return result.ToArray();
                                    case JsonTokenType.String:
                                        result.Add(reader.GetString());
                                        break;
                                    case JsonTokenType.Number:
                                        result.Add(reader.GetDouble());
                                        break;
                                    case JsonTokenType.True:
                                        result.Add(true);
                                        break;
                                    case JsonTokenType.False:
                                        result.Add(false);
                                        break;
                                    case JsonTokenType.Null:
                                        result.Add(null);
                                        break;
                                }
                            }
                            throw new JsonException();
                        }

                        // Try to parse JSON from the received data. If that fails, it must be an error message
                        Utf8JsonReader reader = new(expressionContent);
                        while (result == null && reader.Read())
                        {
                            switch (reader.TokenType)
                            {
                                case JsonTokenType.StartObject:
                                    result = readObject(ref reader);
                                    break;
                                case JsonTokenType.StartArray:
                                    result = readArray(ref reader);
                                    break;
                                case JsonTokenType.String:
                                    result = reader.GetString();
                                    break;
                                case JsonTokenType.Number:
                                    result = reader.GetDouble();
                                    break;
                                case JsonTokenType.True:
                                    result = true;
                                    break;
                                case JsonTokenType.False:
                                    result = false;
                                    break;
                                case JsonTokenType.Null:
                                    result = null;
                                    return AddPadding(bytesRead);
                            }
                        }
                    }
                    catch
                    {
                        string expressionValue = Encoding.UTF8.GetString(expressionContent);
                        result = new CodeParserException(expressionValue);
                    }
                    break;
                case DataType.DateTime:
                    result = DateTime.Parse(Encoding.UTF8.GetString(from.Slice(bytesRead, header.IntValue)));
                    bytesRead += header.IntValue;
                    break;
                case DataType.Null:
                default:
                    result = null;
                    break;
            }

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a <see cref="Request.DoCode"/> request
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel</param>
        /// <param name="code">Code to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadDoCode(ReadOnlySpan<byte> from, out CodeChannel channel, out string code)
        {
            DoCodeHeader header = MemoryMarshal.Read<DoCodeHeader>(from);
            int bytesRead = Marshal.SizeOf<DoCodeHeader>();

            // Read header
            channel = header.Channel;

            // Read code
            ReadOnlySpan<byte> unicodeCode = from.Slice(bytesRead, header.Length);
            code = Encoding.UTF8.GetString(unicodeCode);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a UTF-8 encoded string request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="data">UTF-8 string</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadStringRequest(ReadOnlySpan<byte> from, out ReadOnlySpan<byte> data)
        {
            StringHeader header = MemoryMarshal.Read<StringHeader>(from);
            int bytesRead = Marshal.SizeOf<StringHeader>();

            // Read data
            data = from.Slice(bytesRead, header.Length);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a UTF-8 encoded string request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="data">UTF-8 string</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadStringRequest(ReadOnlySpan<byte> from, out string data)
        {
            StringHeader header = MemoryMarshal.Read<StringHeader>(from);
            int bytesRead = Marshal.SizeOf<StringHeader>();

            // Read data
            data = Encoding.UTF8.GetString(from.Slice(bytesRead, header.Length));
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read an open file request
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filename">Filename to open</param>
        /// <param name="forWriting">Whether the file is supposed to be written to</param>
        /// <param name="append">Whether data is supposed to be appended in write mode</param>
        /// <param name="preAllocSize">How many bytes to allocate if the file is created or overwritten</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadOpenFile(ReadOnlySpan<byte> from, out string filename, out bool forWriting, out bool append, out long preAllocSize)
        {
            OpenFileHeader header = MemoryMarshal.Read<OpenFileHeader>(from);
            int bytesRead = Marshal.SizeOf<OpenFileHeader>();

            // Read header
            forWriting = Convert.ToBoolean(header.ForWriting);
            append = Convert.ToBoolean(header.Append);
            preAllocSize = header.PreAllocSize;

            // Read filename
            ReadOnlySpan<byte> unicodeCode = from.Slice(bytesRead, header.FilenameLength);
            filename = Encoding.UTF8.GetString(unicodeCode);
            bytesRead += header.FilenameLength;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a request to seek in a file
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="handle">File handle</param>
        /// <param name="offset">New file position</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadSeekFile(ReadOnlySpan<byte> from, out uint handle, out long offset)
        {
            SeekFileHeader header = MemoryMarshal.Read<SeekFileHeader>(from);
            handle = header.Handle;
            offset = header.Offset;
            return Marshal.SizeOf<SeekFileHeader>();
        }

        /// <summary>
        /// Read a request to retrieve data from a file
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="handle">File handle</param>
        /// <param name="maxLength">Maximum buffer length</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadFileRequest(ReadOnlySpan<byte> from, out uint handle, out int maxLength)
        {
            ReadFileHeader header = MemoryMarshal.Read<ReadFileHeader>(from);
            handle = header.Handle;
            maxLength = (int)header.MaxLength;
            return Marshal.SizeOf<ReadFileHeader>();
        }

        /// <summary>
        /// Read an arbitrary file handle
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="handle">File handle</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadFileHandle(ReadOnlySpan<byte> from, out uint handle)
        {
            FileHandleHeader header = MemoryMarshal.Read<FileHandleHeader>(from);
            handle = header.Handle;
            return Marshal.SizeOf<FileHandleHeader>();
        }

        /// <summary>
        /// Add padding to a number of read bytes to maintain alignment on a 4-byte boundary
        /// </summary>
        /// <param name="bytesRead">Number of bytes read</param>
        /// <returns>Aligned number of bytes</returns>
        private static int AddPadding(int bytesRead) => ((bytesRead + 3) / 4) * 4;
    }
}