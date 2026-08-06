using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI.ObjectModel;
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
            ResultCode = CodeResult.ErrorNotSupported,
            FragmentNumber = 0x42,
            MoreFollows = true,
            Extra = 0x9A
        };

        // Property round-trips
        Assert.That(reply.RequestId, Is.EqualTo(0xABC));
        Assert.That(reply.ResultCode, Is.EqualTo(CodeResult.ErrorNotSupported));
        Assert.That(reply.FragmentNumber, Is.EqualTo(0x42));
        Assert.That(reply.MoreFollows, Is.True);
        Assert.That(reply.Extra, Is.EqualTo(0x9A));

        // Bit layout: requestId:12, resultCode:4, fragmentNumber:7, moreFollows:1, extra:8
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<CanMessageStandardReply>()];
        MemoryMarshal.Write(bytes, in reply);
        uint expected = 0xABCu | ((uint)CodeResult.ErrorNotSupported << 12) | (0x42u << 16) | (1u << 23) | (0x9Au << 24);
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
        byte[] payload = BuildStandardReplyFragment("Hello ", fragmentNumber: 0, moreFollows: true, resultCode: CodeResult.Warning);
        CanFragment fragment = CanFragmentation.Parse(CanMessageType.StandardReply, payload);

        Assert.That(fragment.Number, Is.EqualTo(0));
        Assert.That(fragment.MoreFollows, Is.True);
        Assert.That(fragment.Extra, Is.Zero);
        Assert.That(fragment.ResultCode, Is.EqualTo(CodeResult.Warning));
        Assert.That(Encoding.UTF8.GetString(fragment.Content), Is.EqualTo("Hello "));
    }

    [Test]
    public void NonFragmentedReplyIsSingleFragment()
    {
        byte[] payload = [1, 2, 3];
        CanFragment fragment = CanFragmentation.Parse(CanMessageType.AnnounceV1, payload);

        Assert.That(fragment.Number, Is.EqualTo(0));
        Assert.That(fragment.MoreFollows, Is.False);
        Assert.That(fragment.ResultCode, Is.Null, "a reply type with no result code of its own must not invent one");
        Assert.That(fragment.Content.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public void ReassembleMultiFragmentReply()
    {
        CanRequest request = new(CanMessageType.Reset, CanMessageType.StandardReply, txToken: 1, dstAddress: 0, isResponse: false, requestPayload: []);

        // Deliver fragments out of order to exercise the ordered reassembly
        byte[] second = BuildStandardReplyFragment("World", fragmentNumber: 1, moreFollows: false);
        byte[] first = BuildStandardReplyFragment("Hello ", fragmentNumber: 0, moreFollows: true);

        foreach (byte[] payload in new[] { second, first })
        {
            CanFragment fragment = CanFragmentation.Parse(CanMessageType.StandardReply, payload);
            request.AddFragment(in fragment);
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
        request.AddFragment(new CanFragment(0, true, 1, CodeResult.Ok, "ab"u8));
        request.AddFragment(new CanFragment(0, false, 2, CodeResult.Error, "XY"u8));  // duplicate fragment number -- ignored
        request.SetResult(CanStatus.Ok, CanMessageType.StandardReply, srcAddress: 0);

        Assert.That(Encoding.UTF8.GetString(request.ResponsePayload), Is.EqualTo("ab"));
        Assert.That(request.Extra, Is.EqualTo(1), "the duplicate must not overwrite the answer either");
        Assert.That(request.ResultCode, Is.EqualTo(CodeResult.Ok), "nor the result code");
    }

    [Test]
    public void TheExtraByteOfTheFirstFragmentIsKept()
    {
        // Creating an input monitor answers in `extra` rather than in the text: it is the input's
        // current state, and the only way to learn a switch that is already closed. The boards report
        // changes, and a switch that never changes never reports one
        byte[] payload = BuildStandardReplyFragment("", fragmentNumber: 0, moreFollows: true, extra: 1);
        CanFragment first = CanFragmentation.Parse(CanMessageType.StandardReply, payload);
        Assert.That(first.Extra, Is.EqualTo(1));

        CanRequest request = new(CanMessageType.Reset, CanMessageType.StandardReply, txToken: 1, dstAddress: 0, isResponse: false, requestPayload: []);
        request.AddFragment(in first);

        // A later fragment carries no answer, so it must not clear the one the first fragment gave
        request.AddFragment(new CanFragment(1, false, 0, CodeResult.Ok, "text"u8));
        request.SetResult(CanStatus.Ok, CanMessageType.StandardReply, srcAddress: 0);
        Assert.That(request.Extra, Is.EqualTo(1));
    }

    [Test]
    public void ARefusedRequestIsAnErrorEvenWithoutText()
    {
        // The board said no and did not say why, which used to read as success because the only thing
        // anyone looked at was whether there was any text
        CanResponse response = Reply(CodeResult.BadOrMissingParameter, "");

        Assert.That(response.Severity, Is.EqualTo(MessageType.Error));
        Assert.That(response.ToMessage().Type, Is.EqualTo(MessageType.Error));
        Assert.That(response.ToMessage().Content, Does.Contain("21").And.Contain("BadOrMissingParameter"));
    }

    [Test]
    public void AWarningKeepsItsTextAndIsNotAFailure()
    {
        CanResponse response = Reply(CodeResult.WarningNotSupported, "M569.7 is not supported");

        Assert.That(response.Severity, Is.EqualTo(MessageType.Warning));
        Assert.That(response.ToMessage().Content, Is.EqualTo("M569.7 is not supported"));
    }

    [Test]
    public void ASuccessfulReplyReportsItsText()
    {
        CanResponse response = Reply(CodeResult.Ok, "Duet3Expansion firmware version 3.7.0");

        Assert.That(response.Severity, Is.EqualTo(MessageType.Success));
        Assert.That(response.Text, Is.EqualTo("Duet3Expansion firmware version 3.7.0"));
    }

    [Test]
    public void AReplyThatNeverArrivedSaysWhichBoardDidNotAnswer()
    {
        CanResponse response = new(CanStatus.Timeout, CanMessageType.StandardReply, SrcAddress: 0, DstAddress: 21,
                                   Payload: [], Extra: 0, ResultCode: null);

        Assert.That(response.Severity, Is.EqualTo(MessageType.Error));
        Assert.That(response.ToMessage().Content, Does.Contain("21").And.Contain("Timeout"));
    }

    [Test]
    public void ARequestExpectingNoReplyHasNothingToReport()
    {
        CanResponse response = new(CanStatus.Ok, CanMessageType.NoReply, SrcAddress: 0, DstAddress: 21,
                                   Payload: [], Extra: 0, ResultCode: null);

        Assert.That(response.Severity, Is.EqualTo(MessageType.Success));
        Assert.That(response.Text, Is.Empty);
        Assert.That(() => response.AsCanMessage<CanMessageHeaterModelReport>(), Throws.InvalidOperationException);
    }

    [Test]
    public void AReplyIsOnlyReadAsTheMessageItWasSentAs()
    {
        CanMessageHeaterModelReport report = new() { HeaterNumber = 3, ResultCode = CodeResult.Ok };
        byte[] payload = new byte[Unsafe.SizeOf<CanMessageHeaterModelReport>()];
        CanMessageSerializer.Serialize(in report, payload);
        CanResponse response = new(CanStatus.Ok, CanMessageType.HeaterModelReport, SrcAddress: 21, DstAddress: 21,
                                   payload, Extra: 0, CodeResult.Ok);

        Assert.That(response.AsCanMessage<CanMessageHeaterModelReport>().HeaterNumber, Is.EqualTo(3));

        // Naming another message type would otherwise read the same bytes as something they are not
        Assert.That(() => response.AsCanMessage<CanMessageReadInputsReplyV1>(), Throws.InvalidOperationException);
    }

    [Test]
    public void ASilentSuccessIsAnEmptyMessageThatCollectingDropsAgain()
    {
        // The one message every caller gets means the decision a warning would be lost by is never
        // theirs to make: a board that did the work without comment is an empty success, which says
        // "done, nothing to report" as a code result and disappears when replies are collected
        Assert.That(Reply(CodeResult.Ok, "").ToMessage().Content, Is.Empty);
        Assert.That(Reply(CodeResult.Warning, "stall threshold clamped").ToMessage().Type, Is.EqualTo(MessageType.Warning));

        Message combined = new[] { Reply(CodeResult.Ok, "").ToMessage(), Reply(CodeResult.Warning, "clamped").ToMessage() }.ToMessage();
        Assert.That(combined.Type, Is.EqualTo(MessageType.Warning));
        Assert.That(combined.Content, Is.EqualTo("clamped"));
    }

    [Test]
    public void CollectedRepliesKeepTheWorstOfWhatTheBoardsSaid()
    {
        Message combined = new Message?[]
        {
            null,
            new Message(MessageType.Success, ""),
            new Message(MessageType.Warning, "board 21: driver 0 not present"),
            new Message(MessageType.Error, "board 22: bad parameter")
        }.ToMessage();

        Assert.That(combined.Type, Is.EqualTo(MessageType.Error));
        Assert.That(combined.Content, Is.EqualTo("board 21: driver 0 not present\nboard 22: bad parameter"));
    }

    /// <summary>A standard reply from board 21, as the link would hand it over once reassembled</summary>
    private static CanResponse Reply(CodeResult resultCode, string text)
        => new(CanStatus.Ok, CanMessageType.StandardReply, SrcAddress: 21, DstAddress: 21,
               Encoding.ASCII.GetBytes(text), Extra: 0, resultCode);

    private static byte[] BuildStandardReplyFragment(string text, byte fragmentNumber, bool moreFollows, byte extra = 0,
                                                     CodeResult resultCode = CodeResult.Ok)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[sizeof(uint) + textBytes.Length];
        uint header = ((uint)resultCode << 12) | ((uint)fragmentNumber << 16) | (moreFollows ? 1u << 23 : 0) | ((uint)extra << 24);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, header);
        textBytes.CopyTo(payload, sizeof(uint));
        return payload;
    }
}
