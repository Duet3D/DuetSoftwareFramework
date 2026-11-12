using System;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetEmulation.Can;

/// <summary>
/// One frame that crossed the emulated CAN bus, as the bus logger recorded it
/// </summary>
/// <param name="Time">Emulated time at which the sender transmitted it</param>
/// <param name="Machine">Name of the sending machine in the emulator</param>
/// <param name="Id">Extended CAN id, which carries the message type, the addresses and the response bit</param>
/// <param name="Data">Payload</param>
/// <param name="IsFd">Whether it was sent as a CAN FD frame</param>
/// <param name="BitRateSwitch">Whether its data phase used the faster bit rate</param>
public sealed record CanFrame(TimeSpan Time, string Machine, uint Id, byte[] Data, bool IsFd, bool BitRateSwitch)
{
    /// <summary>Message type from the CAN id</summary>
    public CanMessageType MessageType => (CanMessageType)((Id >> (int)CanId.MessageTypeShift) & CanId.MessageTypeMask);

    /// <summary>Address of the board that sent it</summary>
    public byte Source => (byte)((Id >> (int)CanId.SrcAddressShift) & CanId.BoardAddressMask);

    /// <summary>Address of the board it is for, or <see cref="CanId.BroadcastAddress"/></summary>
    public byte Destination => (byte)((Id >> (int)CanId.DstAddressShift) & CanId.BoardAddressMask);

    /// <summary>Whether it answers a request</summary>
    public bool IsResponse => (Id & CanId.ResponseBit) != 0;

    /// <summary>
    /// The request id that requests and their standard replies carry in their first 12 bits, or null if
    /// the payload is too short to have one
    /// </summary>
    public ushort? RequestId => Data.Length >= 2 ? (ushort)(BitConverter.ToUInt16(Data, 0) & 0x0FFF) : null;

    /// <summary>Whether this frame is a message of type <typeparamref name="T"/></summary>
    public bool Is<T>() where T : struct, ICanMessage<T> => MessageType == T.MessageType;

    /// <summary>
    /// The payload as a message of type <typeparamref name="T"/>, decoded with the same layouts DSF uses
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame is a different type of message</exception>
    public T As<T>() where T : struct, ICanMessage<T>
    {
        if (!Is<T>())
        {
            throw new InvalidOperationException($"Frame is a {MessageType} message, not {T.MessageType} ({typeof(T).Name})");
        }
        return CanMessageSerializer.Deserialize<T>(Data);
    }

    /// <summary>The payload as a standard reply</summary>
    public CanMessageStandardReply AsStandardReply() => As<CanMessageStandardReply>();

    /// <inheritdoc/>
    public override string ToString()
    {
        string destination = Destination == CanId.BroadcastAddress ? "all" : Destination.ToString();
        string kind = IsResponse ? "response" : "request";
        return $"{Time.TotalSeconds,11:F6} {Source,3} -> {destination,-3} {MessageType} ({kind}) {Convert.ToHexStringLower(Data)}";
    }
}
