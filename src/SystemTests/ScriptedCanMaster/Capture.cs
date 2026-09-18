using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SystemTests;

/// <summary>Which side of the link a captured transfer came from</summary>
internal enum TransferDirection
{
    /// <summary>DuetControlServer to the (fake) controller</summary>
    FromSbc,

    /// <summary>The (fake) controller to DuetControlServer</summary>
    ToSbc,
}

/// <summary>One packet of a captured transfer, with typed decoding for assertions</summary>
internal sealed class CapturedPacket(ushort request, ushort id, byte[] data)
{
    public ushort Request { get; } = request;
    public ushort Id { get; } = id;
    public byte[] Data { get; } = data;

    public SbcRequest SbcRequest => (SbcRequest)Request;
    public FirmwareRequest FirmwareRequest => (FirmwareRequest)Request;

    /// <summary>Decode this packet as a Message request</summary>
    public (uint Flags, string Text) DecodeMessage()
    {
        MessageHeader header = Wire.Read<MessageHeader>(Data);
        return (header.MessageType, Encoding.UTF8.GetString(Data, 8, header.Length));
    }

    /// <summary>Decode this packet as an EnableCAN request</summary>
    public EnableCanHeader DecodeEnableCan() => Wire.Read<EnableCanHeader>(Data);

    /// <summary>Decode this packet as a ScheduleMove request</summary>
    public (ScheduleMoveHeader Header, ScheduleMoveDriver[] Drivers) DecodeScheduleMove()
    {
        ScheduleMoveHeader header = Wire.Read<ScheduleMoveHeader>(Data);
        var drivers = new ScheduleMoveDriver[header.NumDrivers];
        for (int i = 0; i < drivers.Length; i++)
        {
            drivers[i] = Wire.Read<ScheduleMoveDriver>(Data.AsSpan(56 + (i * 16)));
        }
        return (header, drivers);
    }

    /// <summary>Decode this packet as a SendCANMessage request</summary>
    public (SendCanMessageHeader Header, byte[] Payload) DecodeCanMessage()
    {
        SendCanMessageHeader header = Wire.Read<SendCanMessageHeader>(Data);
        return (header, Data.AsSpan(12, header.DataLength).ToArray());
    }
}

/// <summary>
/// One transfer as it crossed the link, in order. Capture is total: every transfer in both
/// directions is recorded, with its header verbatim, so a test can assert on anything the wire
/// carried and a failure can be dumped as a readable exchange log
/// </summary>
internal sealed class CapturedTransfer(TransferDirection direction, TransferHeader header, IReadOnlyList<CapturedPacket> packets)
{
    public TransferDirection Direction { get; } = direction;
    public TransferHeader Header { get; } = header;
    public IReadOnlyList<CapturedPacket> Packets { get; } = packets;

    public override string ToString()
    {
        StringBuilder builder = new();
        builder.Append(Direction == TransferDirection.FromSbc ? "SBC->CTL" : "CTL->SBC");
        builder.Append($" seq={Header.SequenceNumber} clock={Header.MasterClock} len={Header.DataLength}");
        foreach (CapturedPacket packet in Packets)
        {
            string name = Direction == TransferDirection.FromSbc
                ? packet.SbcRequest.ToString()
                : packet.FirmwareRequest.ToString();
            builder.Append($"\n  #{packet.Id} {name} ({packet.Data.Length} bytes)");
            if (Direction == TransferDirection.FromSbc && packet.SbcRequest == SbcRequest.Message)
            {
                (uint flags, string text) = packet.DecodeMessage();
                builder.Append($" flags=0x{flags:X8} \"{text}\"");
            }
        }
        return builder.ToString();
    }

    /// <summary>Render a capture as an exchange log, one line per transfer</summary>
    public static string Dump(IEnumerable<CapturedTransfer> transfers)
        => string.Join('\n', transfers.Select(t => t.ToString()));
}
