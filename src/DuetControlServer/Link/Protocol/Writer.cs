using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.SbcRequests;
using DuetControlServer.Link.Protocol.Shared;
using CodeFlags = DuetControlServer.Link.Protocol.SbcRequests.CodeFlags;
using CodeParameter = DuetControlServer.Link.Protocol.SbcRequests.CodeParameter;

namespace DuetControlServer.Link.Protocol;

/// <summary>
/// Static class for writing data for SPI transmissions.
/// This class makes sure each data block is on a 4-byte boundary to guarantee efficient DMA transfers on the remote side.
/// </summary>
public static class Writer
{
    /// <summary>
    /// Initialize a transfer header
    /// </summary>
    /// <param name="header">Header reference to initialize</param>
    public static void InitTransferHeader(ref TransferHeader header)
    {
        header.FormatCode = Consts.FormatCode;
        header.NumPackets = 0;
        header.ProtocolVersion = Consts.ProtocolVersion;
        header.SequenceNumber = 0;
        header.DataLength = 0;
        header.ChecksumData32 = 0;
        header.ChecksumHeader32 = 0;
    }

    /// <summary>
    /// Write an arbitrary packet header to a memory span
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="request">Packet type</param>
    /// <param name="id">Packet ID</param>
    /// <param name="length">Length of the packet</param>
    public static void WritePacketHeader(Span<byte> to, Request request, ushort id, int length)
    {
        PacketHeader header = new()
        {
            Request = (ushort)request,
            Id = id,
            Length = (ushort)length,
            ResendPacketId = 0
        };
        MemoryMarshal.Write(to, in header);
    }

    /// <summary>
    /// Write a G-code channel
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="channel">Channel for the lock request</param>
    /// <returns>Number of bytes written</returns>
    public static int WriteCodeChannel(Span<byte> to, CodeChannel channel)
    {
        CodeChannelHeader header = new()
        {
            Channel = channel
        };
        MemoryMarshal.Write(to, in header);
        return Marshal.SizeOf<CodeChannelHeader>();
    }

    /// <summary>
    /// Write a <see cref="StringHeader"/> to a memory span
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="data">String data</param>
    /// <returns>Number of bytes written</returns>
    public static int WriteStringRequest(Span<byte> to, string data)
    {
        Span<byte> unicodeData = Encoding.UTF8.GetBytes(data);

        // Write header
        StringHeader request = new()
        {
            Length = (ushort)unicodeData.Length
        };
        MemoryMarshal.Write(to, in request);
        int bytesWritten = Marshal.SizeOf<StringHeader>();

        // Write data
        unicodeData.CopyTo(to[bytesWritten..]);
        bytesWritten += unicodeData.Length;
        return AddPadding(to, bytesWritten);
    }

    /// <summary>
    /// Write a <see cref="MessageHeader"/> to a memory span
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="type">Message flags</param>
    /// <param name="message">Message content</param>
    /// <returns>Number of bytes written</returns>
    public static int WriteMessage(Span<byte> to, MessageTypeFlags type, string message)
    {
        Span<byte> unicodeMessage = Encoding.UTF8.GetBytes(message);

        // Write header
        MessageHeader request = new()
        {
            MessageType = type,
            Length = (ushort)unicodeMessage.Length
        };
        MemoryMarshal.Write(to, in request);
        int bytesWritten = Marshal.SizeOf<MessageHeader>();

        // Write data
        unicodeMessage.CopyTo(to[bytesWritten..]);
        bytesWritten += unicodeMessage.Length;
        return AddPadding(to, bytesWritten);
    }

    /// <summary>
    /// Write an arbitrary boolean value
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="value">Boolean value</param>
    /// <returns>Number of bytes written</returns>
    public static int WriteBoolean(Span<byte> to, bool value)
    {
        BooleanHeader header = new()
        {
            Value = Convert.ToByte(value)
        };
        MemoryMarshal.Write(to, in header);
        return Marshal.SizeOf<BooleanHeader>();
    }

    /// <summary>
    /// Write read file data
    /// </summary>
    /// <param name="to">Destination</param>
    /// <param name="data">Read file data</param>
    /// <param name="bytesRead">Number of bytes read</param>
    /// <returns>Number of bytes written</returns>
    public static int WriteFileReadResult(Span<byte> to, Span<byte> data, int bytesRead)
    {
        // Write header
        FileDataHeader header = new()
        {
            BytesRead = bytesRead
        };
        MemoryMarshal.Write(to, in header);
        int bytesWritten = Marshal.SizeOf<FileDataHeader>();

        // Write content
        data.CopyTo(to[bytesWritten..]);
        bytesWritten += data.Length;

        return AddPadding(to, bytesWritten);
    }

    /// <summary>
    /// Add padding bytes to maintain alignment on a 4-byte boundary
    /// </summary>
    /// <param name="to">Target buffer</param>
    /// <param name="bytesWritten">Number of bytes written so far</param>
    /// <returns>Aligned number of bytes</returns>
    private static int AddPadding(Span<byte> to, int bytesWritten)
    {
        int extraBytes = bytesWritten & 3;
        if (extraBytes == 0)
        {
            return bytesWritten;
        }

        int bytesTotal = bytesWritten + 4 - extraBytes;
        to[bytesWritten..bytesTotal].Fill(0);
        return bytesTotal;
    }
}
