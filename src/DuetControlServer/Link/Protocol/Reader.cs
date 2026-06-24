using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DuetAPI;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol;

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

    public static int ReadMasterClock(ReadOnlySpan<byte> from, out uint masterClock, out uint hiccupTime)
    {
        MasterClockHeader header = MemoryMarshal.Read<MasterClockHeader>(from);
        masterClock = header.MasterClock;
        hiccupTime = header.HiccupTime;
        return Marshal.SizeOf<MasterClockHeader>();
    }

    public static int ReadCANResponse(ReadOnlySpan<byte> from, out ushort txToken)
    {
        CanResponseHeader header = MemoryMarshal.Read<CanResponseHeader>(from);
        txToken = header.TxToken;
        return Marshal.SizeOf<CanResponseHeader>();
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
    /// Add padding to a number of read bytes to maintain alignment on a 4-byte boundary
    /// </summary>
    /// <param name="bytesRead">Number of bytes read</param>
    /// <returns>Aligned number of bytes</returns>
    private static int AddPadding(int bytesRead) => ((bytesRead + 3) / 4) * 4;
}
