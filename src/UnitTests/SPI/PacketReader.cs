using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.SPI;

[TestFixture]
public class PacketReader
{
    [Test]
    public void TransferHeader()
    {
        Span<byte> blob = GetBlob("transferHeader.bin");
        
        TransferHeader header = MemoryMarshal.Read<TransferHeader>(blob);
        
        // Header
        Assert.That(header.FormatCode, Is.EqualTo(Consts.FormatCode));
        Assert.That(header.NumPackets, Is.EqualTo(4));
        Assert.That(header.ProtocolVersion, Is.EqualTo(Consts.ProtocolVersion));
        Assert.That(header.SequenceNumber, Is.EqualTo(12345));
        Assert.That(header.DataLength, Is.EqualTo(1436));
        Assert.That(header.ChecksumData32, Is.EqualTo(0));
        Assert.That(header.ChecksumHeader32, Is.EqualTo(0));
        
        // No padding
    }

    [Test]
    public void PacketHeader()
    {
        Span<byte> blob = GetBlob("packetHeader.bin");
        
        int bytesRead = Reader.ReadPacketHeader(blob, out PacketHeader header);
        Assert.That(bytesRead, Is.EqualTo(8));
        
        // Header
        Assert.That(header.Request, Is.EqualTo((ushort)Request.ObjectModel));
        Assert.That(header.Id, Is.EqualTo(12));
        Assert.That(header.Length, Is.EqualTo(300));
    }

    [Test]
    public void PacketHeaderResend()
    {
        Span<byte> blob = GetBlob("packetHeaderResend.bin");

        int bytesRead = Reader.ReadPacketHeader(blob, out PacketHeader header);
        Assert.That(bytesRead, Is.EqualTo(8));

        // Header
        Assert.That(header.Request, Is.EqualTo((ushort)Request.ResendPacket));
        Assert.That(header.Id, Is.EqualTo(23));
        Assert.That(header.Length, Is.EqualTo(0));
        Assert.That(header.ResendPacketId, Is.EqualTo(12));
    }

    [Test]
    public void StringRequest()
    {
        Span<byte> blob = GetBlob("stringRequest.bin");
        
        int bytesRead = Reader.ReadStringRequest(blob, out ReadOnlySpan<byte> json);
        Assert.That(bytesRead, Is.EqualTo(24));
        
        // JSON
        Assert.That(Encoding.UTF8.GetString(json), Is.EqualTo("{\"hello\":\"json!\"}"));
    }

    [Test]
    public void CodeBufferUpdate()
    {
        Span<byte> blob = GetBlob("codeBufferUpdate.bin");

        int bytesRead = Reader.ReadCodeBufferUpdate(blob, out ushort bufferSpace);
        Assert.That(bytesRead, Is.EqualTo(4));

        // Header
        Assert.That(bufferSpace, Is.EqualTo(787));
    }

    [Test]
    public void Message()
    {
        Span<byte> blob = GetBlob("message.bin");

        int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
        Assert.That(bytesRead, Is.EqualTo(28));

        // Header
        Assert.That(messageType.HasFlag(MessageTypeFlags.HttpMessage), Is.True);
        Assert.That(messageType.HasFlag(MessageTypeFlags.TelnetMessage), Is.True);
        Assert.That(messageType.HasFlag(MessageTypeFlags.UsbMessage), Is.True);
        Assert.That(messageType.HasFlag(MessageTypeFlags.AuxMessage), Is.True);
        Assert.That(messageType.HasFlag(MessageTypeFlags.WarningMessageFlag), Is.True);
        Assert.That(messageType.HasFlag(MessageTypeFlags.PushFlag), Is.True);
        
        // Message
        Assert.That(reply, Is.EqualTo("This is just a test"));
    }
    
    [Test]
    public void EmptyMessage()
    {
        Span<byte> blob = GetBlob("emptyMessage.bin");

        int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
        Assert.That(bytesRead, Is.EqualTo(8));

        // Header
        Assert.That(messageType.HasFlag(MessageTypeFlags.UsbMessage), Is.True);

        // Message
        Assert.That(reply, Is.Empty);
    }
    
    [Test]
    public void MacroRequest()
    {
        Span<byte> blob = GetBlob("macroRequest.bin");
        
        int bytesRead = Reader.ReadMacroRequest(blob, out CodeChannel channel, out bool fromCode, out string filename);
        Assert.That(bytesRead, Is.EqualTo(16));
        
        // Header
        Assert.That(channel, Is.EqualTo(DuetAPI.CodeChannel.USB));
        Assert.That(fromCode, Is.True);
        
        // Message
        Assert.That(filename, Is.EqualTo("homeall.g"));
    }

    [Test]
    public void AbortFile()
    {
        Span<byte> blob = GetBlob("abortFile.bin");

        int bytesRead = Reader.ReadAbortFile(blob, out CodeChannel channel, out bool abortAll);
        Assert.That(bytesRead, Is.EqualTo(4));

        // Header
        Assert.That(channel, Is.EqualTo(DuetAPI.CodeChannel.File));
        Assert.That(abortAll, Is.False);
    }

    [Test]
    public void PrintPaused()
    {
        Span<byte> blob = GetBlob("printPaused.bin");
        
        int bytesRead = Reader.ReadPrintPaused(blob, out uint filePosition, out uint filePosition2, out PrintPausedReason reason);
        Assert.That(bytesRead, Is.EqualTo(12));
        
        // Header
        Assert.That(filePosition, Is.EqualTo(123456));
        Assert.That(filePosition2, Is.EqualTo(123456));
        Assert.That(reason, Is.EqualTo(PrintPausedReason.GCode));
    } 

    [Test]
    public void CodeChannel()
    {
        Span<byte> blob = GetBlob("codeChannel.bin");

        int bytesRead = Reader.ReadCodeChannel(blob, out CodeChannel channel);
        Assert.That(bytesRead, Is.EqualTo(4));

        // Header
        Assert.That(channel, Is.EqualTo(DuetAPI.CodeChannel.SBC));
    }

    [Test]
    public void FileChunk()
    {
        Span<byte> blob = GetBlob("fileChunk.bin");

        int bytesRead = Reader.ReadFileChunkRequest(blob, out string filename, out uint offset, out int maxLength);
        Assert.That(bytesRead, Is.EqualTo(20));

        // Header
        Assert.That(offset, Is.EqualTo(1234));
        Assert.That(maxLength, Is.EqualTo(5678));

        // Filename
        Assert.That(filename, Is.EqualTo("test.bin"));
    }

    [Test]
    public void EvaluationResult()
    {
        Span<byte> blob = GetBlob("evaluationResult.bin");

        int bytesRead = Reader.ReadEvaluationResult(blob, out CodeChannel channel, out string expression, out object result);
        Assert.That(bytesRead, Is.EqualTo(32));

        // Header
        Assert.That(channel, Is.EqualTo(DuetAPI.CodeChannel.HTTP));
        Assert.That((int)result!, Is.EqualTo(300));

        // Expression
        Assert.That(expression, Is.EqualTo("move.axes[0].position"));
    }

    [Test]
    public void DoCode()
    {
        Span<byte> blob = GetBlob("doCode.bin");

        int bytesRead = Reader.ReadDoCode(blob, out CodeChannel channel, out string code);
        Assert.That(bytesRead, Is.EqualTo(24));

        // Header
        Assert.That(channel, Is.EqualTo(DuetAPI.CodeChannel.Aux));

        // Code
        Assert.That(code, Is.EqualTo("M20 S2 P\"0:/macros\""));
    }

    private static Span<byte> GetBlob(string filename)
    {
        FileStream stream = new(Path.Combine(Directory.GetCurrentDirectory(), "../../../SPI/Blobs", filename), FileMode.Open, FileAccess.Read);
        Span<byte> content = new byte[stream.Length];
        stream.ReadExactly(content);
        stream.Close();
        return content;
    }
}