using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

/// <summary>
/// Tests for splitting the data of a transfer into packets.
/// The adapters are created without their constructors, which open the devices, and get the receive state set directly
/// </summary>
[TestFixture]
public class PacketFraming
{
    private static readonly int PacketHeaderSize = Marshal.SizeOf<PacketHeader>();

    [TestCase(typeof(DuetControlServer.Link.Adapter.SPI))]
    [TestCase(typeof(USB))]
    public void PacketsFillingTheTransfer(Type adapterType)
    {
        // Two packets, the first one padded to the next 4-byte boundary
        byte[] data = new byte[2 * PacketHeaderSize + 8];
        WritePacketHeader(data, 0, 1, 5);
        WritePacketHeader(data, PacketHeaderSize + 8, 2, 0);
        ILinkAdapter adapter = CreateAdapter(adapterType, data, data.Length);

        Assert.That(adapter.ReadNextPacket()?.Id, Is.EqualTo(1));
        Assert.That(GetPacketData(adapter).Length, Is.EqualTo(5));
        Assert.That(adapter.ReadNextPacket()?.Id, Is.EqualTo(2));
        Assert.That(GetPacketData(adapter).Length, Is.EqualTo(0));
        Assert.That(adapter.ReadNextPacket(), Is.Null);
    }

    [TestCase(typeof(DuetControlServer.Link.Adapter.SPI))]
    [TestCase(typeof(USB))]
    public void PacketDataPastTheTransfer(Type adapterType)
    {
        // The packet claims 16 bytes but the transfer ends after 4, followed by data of an earlier transfer
        byte[] buffer = new byte[64];
        buffer.AsSpan().Fill(0xAA);
        WritePacketHeader(buffer, 0, 1, 16);
        ILinkAdapter adapter = CreateAdapter(adapterType, buffer, PacketHeaderSize + 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.ReadNextPacket());
    }

    [TestCase(typeof(DuetControlServer.Link.Adapter.SPI))]
    [TestCase(typeof(USB))]
    public void PacketHeaderPastTheTransfer(Type adapterType)
    {
        // The last 4 bytes of the transfer cannot hold a packet header, but together with data of an earlier transfer they look like one
        byte[] buffer = new byte[64];
        buffer.AsSpan().Fill(0xAA);
        WritePacketHeader(buffer, 0, 1, 0);
        WritePacketHeader(buffer, PacketHeaderSize, 9, 0);
        ILinkAdapter adapter = CreateAdapter(adapterType, buffer, PacketHeaderSize + 4);

        Assert.That(adapter.ReadNextPacket()?.Id, Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => adapter.ReadNextPacket());
    }

    private static void WritePacketHeader(byte[] buffer, int offset, ushort id, ushort length)
    {
        PacketHeader header = new() { Request = 1, Id = id, Length = length };
        MemoryMarshal.Write(buffer.AsSpan(offset), in header);
    }

    /// <summary>
    /// Create an adapter that has received the given data
    /// </summary>
    private static ILinkAdapter CreateAdapter(Type adapterType, byte[] buffer, int dataLength)
    {
        object adapter = RuntimeHelpers.GetUninitializedObject(adapterType);
        SetField(adapter, "_rxBuffer", (Memory<byte>)buffer);
        SetField(adapter, "_rxPointer", 0);
        if (adapterType == typeof(USB))
        {
            SetField(adapter, "_rxHeader", new UsbTransferHeader { DataLength = (ushort)dataLength });
        }
        else
        {
            SetField(adapter, "_rxHeader", new TransferHeader { DataLength = (ushort)dataLength });
        }
        return (ILinkAdapter)adapter;
    }

    private static ReadOnlyMemory<byte> GetPacketData(ILinkAdapter adapter) => (ReadOnlyMemory<byte>)GetField(adapter, "_packetData")!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
