using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;

namespace UnitTests.Link;

/// <summary>
/// Tests for interpreting a raw CAN payload as a message body
/// </summary>
/// <remarks>
/// The payload a board sends is not always the size of the struct that models it. Several message
/// types are variable length by design - a fixed part followed by as many entries as the board has
/// something to report - so the length of what arrives is data, not a mismatch to be refused
/// </remarks>
[TestFixture]
public class CanMessageSerializerTests
{
    /// <summary>
    /// Build a board status report the way a board does: the fixed part, only the min/current/max
    /// values the board actually has, then one entry per analog handle
    /// </summary>
    /// <param name="hasVin">Whether the board reports its input voltage</param>
    /// <param name="handles">Reading of each analog handle, in order</param>
    /// <returns>The payload</returns>
    private static byte[] BoardStatusPayload(bool hasVin, params int[] handles)
    {
        CanMessageBoardStatusV1 status = default;
        status.HasVin = hasVin;
        status.NumAnalogHandles = (byte)handles.Length;

        int offset = (int)status.GetAnalogHandlesOffset();
        byte[] payload = new byte[offset + (handles.Length * 8)];

        // The fixed part is written from the struct, then truncated to where the readings begin: the
        // struct always has room for three min/current/max values, the wire only for those present
        byte[] fixedPart = new byte[Marshal.SizeOf<CanMessageBoardStatusV1>()];
        MemoryMarshal.Write(fixedPart, in status);
        fixedPart.AsSpan(0, offset).CopyTo(payload);

        for (int i = 0; i < handles.Length; i++)
        {
            AnalogHandleDataV1 data = default;
            data.Handle.Type = (byte)RemoteInputHandle.TypeZprobe;
            data.Handle.Major = (byte)i;
            data.Reading = handles[i];
            MemoryMarshal.Write(payload.AsSpan(offset + (i * 8)), in data);
        }
        return payload;
    }

    [Test]
    public void APayloadLongerThanTheStructIsStillRead()
    {
        // A board status report is the fixed part plus one entry per analog handle, so a board with
        // anything to report sends more bytes than the struct has. Refusing it would throw away the
        // board's voltage, temperature and state along with the readings
        byte[] payload = BoardStatusPayload(hasVin: true, 100, 200, 300);
        Assert.That(payload, Has.Length.GreaterThan(Marshal.SizeOf<CanMessageBoardStatusV1>()));

        CanMessageBoardStatusV1 status = CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(payload);
        Assert.Multiple(() =>
        {
            Assert.That(status.HasVin, Is.True);
            Assert.That(status.NumAnalogHandles, Is.EqualTo(3));
        });
    }

    [Test]
    public void AShortPayloadIsZeroPaddedRatherThanRefused()
    {
        // A board with no voltage or temperature monitoring sends only the fixed header, which is
        // shorter than the struct because the struct reserves all three min/current/max slots
        byte[] payload = BoardStatusPayload(hasVin: false);
        Assert.That(payload, Has.Length.LessThan(Marshal.SizeOf<CanMessageBoardStatusV1>()));

        CanMessageBoardStatusV1 status = CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(payload);
        Assert.Multiple(() =>
        {
            Assert.That(status.HasVin, Is.False);
            Assert.That(status.NumAnalogHandles, Is.Zero);
        });
    }

    [Test]
    public void TheReadingsStartWhereTheBoardSaysTheyDo()
    {
        // Where the readings begin depends on how many min/current/max values the board has, not on
        // the size of the struct. Reading them at a fixed offset would give a board without voltage
        // monitoring somebody else's numbers
        byte[] withVin = BoardStatusPayload(hasVin: true, 111);
        byte[] withoutVin = BoardStatusPayload(hasVin: false, 111);

        CanMessageBoardStatusV1 a = CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(withVin);
        CanMessageBoardStatusV1 b = CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(withoutVin);

        Assert.Multiple(() =>
        {
            Assert.That(a.GetAnalogHandlesOffset(), Is.EqualTo(8 + 6), "the fixed part plus one reading");
            Assert.That(b.GetAnalogHandlesOffset(), Is.EqualTo(8), "the fixed part alone");
            Assert.That(a.GetActualDataLength(), Is.EqualTo(withVin.Length));
            Assert.That(b.GetActualDataLength(), Is.EqualTo(withoutVin.Length));
        });
    }

    [Test]
    public void EachReadingCarriesTheHandleItBelongsTo()
    {
        // The entries say which input they are for; nothing about their position does. Losing the
        // handle would leave a reading that cannot be applied to anything
        byte[] payload = BoardStatusPayload(hasVin: true, 500, 600);
        CanMessageBoardStatusV1 status = CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(payload);

        int offset = (int)status.GetAnalogHandlesOffset();
        AnalogHandleDataV1 second = MemoryMarshal.Read<AnalogHandleDataV1>(payload.AsSpan(offset + 8));
        Assert.Multiple(() =>
        {
            Assert.That(second.Handle.Type, Is.EqualTo(RemoteInputHandle.TypeZprobe));
            Assert.That(second.Handle.Major, Is.EqualTo(1));
            Assert.That(second.Reading, Is.EqualTo(600));
        });
    }
}
