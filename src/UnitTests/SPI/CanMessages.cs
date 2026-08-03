using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

[TestFixture]
public class CanMessages
{
    [Test]
    public void StructSizesMatchCanLib()
    {
        // These must match sizeof() of the corresponding structs in CANlib's CanMessageFormats.h
        Assert.That(Unsafe.SizeOf<CanMessageReset>(), Is.EqualTo(2));
        Assert.That(Unsafe.SizeOf<CanMessageStandardReply>(), Is.EqualTo(64));
        Assert.That(Unsafe.SizeOf<CanMessageAnnounceV1>(), Is.EqualTo(64));
    }

    [Test]
    public void ResetBitfields()
    {
        CanMessageReset reset = new() { RequestId = 0xABC };
        Assert.That(reset.RequestId, Is.EqualTo(0xABC));

        // RequestId must occupy the low 12 bits of the 16-bit word
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<CanMessageReset>()];
        MemoryMarshal.Write(bytes, in reset);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes), Is.EqualTo(0x0ABC));
    }

    [Test]
    public void StandardReplyBitfields()
    {
        CanMessageStandardReply reply = new()
        {
            RequestId = 0xABC,
            ResultCode = 0x5,
            FragmentNumber = 0x42,
            MoreFollows = true,
            Extra = 0x9A
        };

        // Property round-trips
        Assert.That(reply.RequestId, Is.EqualTo(0xABC));
        Assert.That(reply.ResultCode, Is.EqualTo(0x5));
        Assert.That(reply.FragmentNumber, Is.EqualTo(0x42));
        Assert.That(reply.MoreFollows, Is.True);
        Assert.That(reply.Extra, Is.EqualTo(0x9A));

        // Bit layout: requestId:12, resultCode:4, fragmentNumber:7, moreFollows:1, extra:8
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<CanMessageStandardReply>()];
        MemoryMarshal.Write(bytes, in reply);
        uint expected = 0xABCu | (0x5u << 12) | (0x42u << 16) | (1u << 23) | (0x9Au << 24);
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Is.EqualTo(expected));
    }

    [Test]
    public void AnnounceV1Bitfields()
    {
        CanMessageAnnounceV1 announce = new() { NumDrivers = 0xB, UsesUf2Binary = true };
        Assert.That(announce.NumDrivers, Is.EqualTo(0xB));
        Assert.That(announce.UsesUf2Binary, Is.True);

        // numDrivers:4, usesUf2Binary:1 share the byte at offset 20 (4 + 16)
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<CanMessageAnnounceV1>()];
        MemoryMarshal.Write(bytes, in announce);
        Assert.That(bytes[20], Is.EqualTo(0x1B));
    }

    [Test]
    public void FirmwareUpdateRequestCanBeDeserializedFromShortPayload()
    {
        byte[] payload = [
            0x56, 0x34, 0x12, 0x01,
            0x78, 0x56, 0x34, 0x00,
            (byte)'E', (byte)'X', (byte)'P', (byte)'1', (byte)'H', (byte)'C', (byte)'L', 0x00
        ];

        CanMessageFirmwareUpdateRequest request = CanMessageSerializer.Deserialize<CanMessageFirmwareUpdateRequest>(payload);

        Assert.That(request.FileOffset, Is.EqualTo(0x123456));
        Assert.That(request.BootloaderVersion, Is.EqualTo(0x01));
        Assert.That(request.Uf2Format, Is.False);
        Assert.That(request.FileWanted, Is.EqualTo(0x00));
        Assert.That(request.LengthRequested, Is.EqualTo(0x345678));
        Assert.That(request.BoardVersion, Is.EqualTo(0x00));
        Assert.That(request.BoardTypeString, Is.EqualTo("EXP1HCL"));

        Span<byte> boardTypeBytes = stackalloc byte[56];
        MemoryMarshal.Write(boardTypeBytes, in request.BoardType);
        Assert.That(boardTypeBytes[..8].ToArray(), Is.EqualTo(new byte[] { (byte)'E', (byte)'X', (byte)'P', (byte)'1', (byte)'H', (byte)'C', (byte)'L', 0x00 }));
        Assert.That(boardTypeBytes[8..].ToArray(), Is.EqualTo(new byte[48]));
    }

    [Test]
    public void StandardReplyFragmentInfo()
    {
        byte[] fragment = BuildStandardReplyFragment("Hello ", fragmentNumber: 0, moreFollows: true);
        CanFragmentation.GetFragmentInfo(CanMessageType.StandardReply, fragment, out int fragmentNumber, out bool moreFollows, out ReadOnlySpan<byte> content);

        Assert.That(fragmentNumber, Is.EqualTo(0));
        Assert.That(moreFollows, Is.True);
        Assert.That(Encoding.UTF8.GetString(content), Is.EqualTo("Hello "));
    }

    [Test]
    public void NonFragmentedReplyIsSingleFragment()
    {
        byte[] payload = [1, 2, 3];
        CanFragmentation.GetFragmentInfo(CanMessageType.AnnounceV1, payload, out int fragmentNumber, out bool moreFollows, out ReadOnlySpan<byte> content);

        Assert.That(fragmentNumber, Is.EqualTo(0));
        Assert.That(moreFollows, Is.False);
        Assert.That(content.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void ReassembleMultiFragmentReply()
    {
        CanRequest request = new(CanMessageType.Reset, CanMessageType.StandardReply, txToken: 1, dstAddress: 0, isResponse: false, requestPayload: []);

        // Deliver fragments out of order to exercise the ordered reassembly
        byte[] second = BuildStandardReplyFragment("World", fragmentNumber: 1, moreFollows: false);
        byte[] first = BuildStandardReplyFragment("Hello ", fragmentNumber: 0, moreFollows: true);

        foreach (byte[] fragment in new[] { second, first })
        {
            CanFragmentation.GetFragmentInfo(CanMessageType.StandardReply, fragment, out int fragmentNumber, out _, out ReadOnlySpan<byte> content);
            request.AddFragment(fragmentNumber, content);
        }

        request.SetResult(CanStatus.Ok, CanMessageType.StandardReply, srcAddress: 7);

        Assert.That(request.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(request.Status, Is.EqualTo(CanStatus.Ok));
        Assert.That(request.SrcAddress, Is.EqualTo(7));
        Assert.That(Encoding.UTF8.GetString(request.ResponsePayload), Is.EqualTo("Hello World"));
    }

    [Test]
    public void DuplicateFragmentIgnored()
    {
        CanRequest request = new(CanMessageType.Reset, CanMessageType.StandardReply, txToken: 1, dstAddress: 0, isResponse: false, requestPayload: []);
        request.AddFragment(0, "ab"u8);
        request.AddFragment(0, "XY"u8);     // duplicate fragment number -- ignored
        request.SetResult(CanStatus.Ok, CanMessageType.StandardReply, srcAddress: 0);

        Assert.That(Encoding.UTF8.GetString(request.ResponsePayload), Is.EqualTo("ab"));
    }

    private static byte[] BuildStandardReplyFragment(string text, byte fragmentNumber, bool moreFollows)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[sizeof(uint) + textBytes.Length];
        uint header = ((uint)fragmentNumber << 16) | (moreFollows ? 1u << 23 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, header);
        textBytes.CopyTo(payload, sizeof(uint));
        return payload;
    }
}
