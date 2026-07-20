using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Protocol.SbcRequests;
using NUnit.Framework;
using CodeFlags = DuetControlServer.Link.Protocol.SbcRequests.CodeFlags;

namespace UnitTests.SPI;

[TestFixture]
public class PacketWriter
{
    [Test]
    public void TransferHeader()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        TransferHeader header = MemoryMarshal.Read<TransferHeader>(span);
        Writer.InitTransferHeader(ref header);

        // Header
        Assert.That(header.FormatCode, Is.EqualTo(Consts.FormatCode));
        Assert.That(header.NumPackets, Is.EqualTo(0));
        Assert.That(header.ProtocolVersion, Is.EqualTo(Consts.ProtocolVersion));
        Assert.That(header.SequenceNumber, Is.EqualTo(0));
        Assert.That(header.DataLength, Is.EqualTo(0));
        Assert.That(header.ChecksumData32, Is.EqualTo(0));
        Assert.That(header.ChecksumHeader32, Is.EqualTo(0));

        // No padding
    }

    [Test]
    public void PacketHeader()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        Writer.WritePacketHeader(span, Request.Reset, 12, 1054);

        // Header
        ushort request = MemoryMarshal.Read<ushort>(span[..2]);
        Assert.That(request, Is.EqualTo((ushort)Request.Reset));
        ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
        Assert.That(packetId, Is.EqualTo(12));
        ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
        Assert.That(packetLength, Is.EqualTo(1054));

        // Padding
        Assert.That(span[6], Is.EqualTo(0));
        Assert.That(span[7], Is.EqualTo(0));
    }

    [Test]
    public void SimpleCode()
    {
        Span<byte> span = new byte[128];

        var code = new DuetAPI.Commands.Code("G53 G10")
        {
            Channel = DuetAPI.CodeChannel.HTTP
        };

        int bytesWritten = Writer.WriteCode(span, code, Consts.ProtocolVersion);
        Assert.That(bytesWritten, Is.EqualTo(20));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.HTTP));
        Assert.That(span[1], Is.EqualTo((byte)(CodeFlags.HasMajorCommandNumber | CodeFlags.EnforceAbsolutePosition)));
        Assert.That(span[2], Is.EqualTo(0));                    // Number of parameters
        byte codeLetter = (byte)'G';
        Assert.That(span[3], Is.EqualTo(codeLetter));
        int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
        Assert.That(majorCode, Is.EqualTo(10));
        int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
        Assert.That(minorCode, Is.EqualTo(0));
        uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
        Assert.That(filePosition, Is.EqualTo(0xFFFFFFFF));

        // Line number (protocol v2+)
        int lineNumber = MemoryMarshal.Read<int>(span.Slice(16, 4));
        Assert.That(lineNumber, Is.EqualTo(0));

        // No padding
    }

    [Test]
    public void CodeWithParameters()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        var code = new DuetAPI.Commands.Code("G1 X4 Y23.5 Z12.2 J\"testok\" E12:3.45:5.67")
        {
            Channel = DuetAPI.CodeChannel.File
        };

        int bytesWritten = Writer.WriteCode(span, code, Consts.ProtocolVersion);
        Assert.That(bytesWritten, Is.EqualTo(80));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.File));
        Assert.That(span[1], Is.EqualTo((byte)CodeFlags.HasMajorCommandNumber));
        Assert.That(span[2], Is.EqualTo(5));                    // Number of parameters
        Assert.That(span[3], Is.EqualTo((byte)'G'));            // Code letter
        int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
        Assert.That(majorCode, Is.EqualTo(1));
        int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
        Assert.That(minorCode, Is.EqualTo(0));
        uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
        Assert.That(filePosition, Is.EqualTo(0xFFFFFFFF));

        // Line number (protocol v2+)
        int lineNumber = MemoryMarshal.Read<int>(span.Slice(16, 4));
        Assert.That(lineNumber, Is.EqualTo(0));

        // First parameter (X4)
        Assert.That(span[20], Is.EqualTo((byte)'X'));
        Assert.That(span[21], Is.EqualTo((byte)DataType.Int));
        int intValue = MemoryMarshal.Read<int>(span.Slice(24, 4));
        Assert.That(intValue, Is.EqualTo(4));

        // Second parameter (Y23.5)
        Assert.That(span[28], Is.EqualTo((byte)'Y'));
        Assert.That(span[29], Is.EqualTo((byte)DataType.Float));
        float floatValue = MemoryMarshal.Read<float>(span.Slice(32, 4));
        Assert.That(floatValue, Is.EqualTo(23.5).Within(0.00001));

        // Third parameter (Z12.2)
        Assert.That(span[36], Is.EqualTo((byte)'Z'));
        Assert.That(span[37], Is.EqualTo((byte)DataType.Float));
        floatValue = MemoryMarshal.Read<float>(span.Slice(40, 4));
        Assert.That(floatValue, Is.EqualTo(12.2).Within(0.00001));

        // Fourth parameter (J"testok")
        Assert.That(span[44], Is.EqualTo((byte)'J'));
        Assert.That(span[45], Is.EqualTo((byte)DataType.String));
        intValue = MemoryMarshal.Read<int>(span.Slice(48, 4));
        Assert.That(intValue, Is.EqualTo(6));

        // Fifth parameter (E12:3.45:5.67)
        Assert.That(span[52], Is.EqualTo((byte)'E'));
        Assert.That(span[53], Is.EqualTo((byte)DataType.FloatArray));
        intValue = MemoryMarshal.Read<int>(span.Slice(56, 4));
        Assert.That(intValue, Is.EqualTo(3));

        // Payload of fourth parameter ("test")
        string stringValue = Encoding.UTF8.GetString(span.Slice(60, 6));
        Assert.That(stringValue, Is.EqualTo("testok"));
        Assert.That(span[66], Is.EqualTo(0));
        Assert.That(span[67], Is.EqualTo(0));

        // Payload of fifth parameter (12:3.45:5.67)
        floatValue = MemoryMarshal.Read<float>(span.Slice(68, 4));
        Assert.That(floatValue, Is.EqualTo(12).Within(0.00001));
        floatValue = MemoryMarshal.Read<float>(span.Slice(72, 4));
        Assert.That(floatValue, Is.EqualTo(3.45).Within(0.00001));
        floatValue = MemoryMarshal.Read<float>(span.Slice(76, 4));
        Assert.That(floatValue, Is.EqualTo(5.67).Within(0.00001));
    }

    [Test]
    public void Comment()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        var code = new DuetAPI.Commands.Code("; Hello world")
        {
            Channel = DuetAPI.CodeChannel.Telnet
        };

        int bytesWritten = Writer.WriteCode(span, code, Consts.ProtocolVersion);
        Assert.That(bytesWritten, Is.EqualTo(40));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.Telnet));
        Assert.That(span[1], Is.EqualTo((byte)CodeFlags.HasMajorCommandNumber));
        Assert.That(span[2], Is.EqualTo(1));                    // Number of parameters
        Assert.That(span[3], Is.EqualTo((byte)'Q'));            // Code letter
        int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
        Assert.That(majorCode, Is.EqualTo(0));
        int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
        Assert.That(minorCode, Is.EqualTo(0));
        uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
        Assert.That(filePosition, Is.EqualTo(0xFFFFFFFF));

        // Line number (protocol v2+)
        int lineNumber = MemoryMarshal.Read<int>(span.Slice(16, 4));
        Assert.That(lineNumber, Is.EqualTo(0));

        // Comment parameter
        Assert.That(span[20], Is.EqualTo((byte)'@'));
        Assert.That(span[21], Is.EqualTo((byte)DataType.String));
        int intValue = MemoryMarshal.Read<int>(span.Slice(24, 4));
        Assert.That(intValue, Is.EqualTo(11));

        // Comment payload ("Hello world")
        string stringValue = Encoding.UTF8.GetString(span.Slice(28, 11));
        Assert.That(stringValue, Is.EqualTo("Hello world"));
        Assert.That(span[39], Is.EqualTo(0));
    }

    [Test]
    public void GetObjectModel()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteGetObjectModel(span, "move", "d99vn");
        Assert.That(bytesWritten, Is.EqualTo(16));

        // Header
        Assert.That(MemoryMarshal.Read<ushort>(span), Is.EqualTo(4));               // Key length
        Assert.That(MemoryMarshal.Read<ushort>(span.Slice(2, 2)), Is.EqualTo(5));   // Flags length

        // Key
        string key = Encoding.UTF8.GetString(span.Slice(4, 4));
        Assert.That(key, Is.EqualTo("move"));

        // Flags
        string flags = Encoding.UTF8.GetString(span.Slice(8, 5));
        Assert.That(flags, Is.EqualTo("d99vn"));

        // Padding
        Assert.That(span[13], Is.EqualTo(0));
        Assert.That(span[14], Is.EqualTo(0));
        Assert.That(span[15], Is.EqualTo(0));
    }

    [Test]
    public void SetObjectModel()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteSetObjectModel(span, "foobar", "myval");
        Assert.That(bytesWritten, Is.EqualTo(24));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DataType.String));
        Assert.That(span[1], Is.EqualTo(6));                        // Field path length
        int intValue = MemoryMarshal.Read<int>(span.Slice(4, 4));
        Assert.That(intValue, Is.EqualTo(5));

        // Field path
        string field = Encoding.UTF8.GetString(span.Slice(8, 6));
        Assert.That(field, Is.EqualTo("foobar"));
        Assert.That(span[14], Is.EqualTo(0));
        Assert.That(span[15], Is.EqualTo(0));

        // Field value
        string value = Encoding.UTF8.GetString(span.Slice(16, 5));
        Assert.That(value, Is.EqualTo("myval"));

        // Padding
        Assert.That(span[21], Is.EqualTo(0));
        Assert.That(span[22], Is.EqualTo(0));
        Assert.That(span[23], Is.EqualTo(0));
    }

    [Test]
    public void SetPrintFileInfo()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        GCodeFileInfo info = new()
        {
            Size = 452432,
            FileName = "0:/gcodes/test.g",
            GeneratedBy = "Slic3r",
            Height = 53.4F,
            NumLayers = 16,
            LayerHeight = 0.2F,
            PrintTime = 12355,
            SimulatedTime = 10323
        };
        info.Filament.Add(123.45F);
        info.Filament.Add(678.9F);

        int bytesWritten = Writer.WritePrintFileInfo(span, info);
        Assert.That(bytesWritten, Is.EqualTo(72));

        // Header
        ushort filenameLength = MemoryMarshal.Read<ushort>(span[..2]);
        Assert.That(filenameLength, Is.EqualTo(info.FileName.Length));
        ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
        Assert.That(generatedByLength, Is.EqualTo(6));
        uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
        Assert.That(numFilaments, Is.EqualTo(2));
        uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
        Assert.That(fileSize, Is.EqualTo(452432));
        uint numLayers = MemoryMarshal.Read<uint>(span.Slice(20, 4));
        Assert.That(numLayers, Is.EqualTo(16));
        float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
        Assert.That(layerHeight, Is.EqualTo(0.2).Within(0.00001));
        float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
        Assert.That(objectHeight, Is.EqualTo(53.4).Within(0.00001));
        uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
        Assert.That(printTime, Is.EqualTo(12355));
        uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
        Assert.That(simulatedTime, Is.EqualTo(10323));

        // Filament consumption
        float filamentUsageA = MemoryMarshal.Read<float>(span.Slice(40, 4));
        Assert.That(filamentUsageA, Is.EqualTo(123.45).Within(0.0001));
        float filamentUsageB = MemoryMarshal.Read<float>(span.Slice(44, 4));
        Assert.That(filamentUsageB, Is.EqualTo(678.9).Within(0.0001));

        // File name
        string fileName = Encoding.UTF8.GetString(span.Slice(48, info.FileName.Length));
        Assert.That(fileName, Is.EqualTo(info.FileName));

        // Generated by
        string generatedBy = Encoding.UTF8.GetString(span.Slice(48 + info.FileName.Length, info.GeneratedBy.Length));
        Assert.That(generatedBy, Is.EqualTo(info.GeneratedBy));
    }

    [Test]
    public void SetPrintFileInfo2()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        GCodeFileInfo info = new()
        {
            Size = 4180,
            FileName = "0:/gcodes/circle.g",
            NumLayers = 0,
            GeneratedBy = string.Empty,
            Height = 0,
            LayerHeight = 0,
            PrintTime = 0,
            SimulatedTime = 0,
        };

        int bytesWritten = Writer.WritePrintFileInfo(span, info);
        Assert.That(bytesWritten, Is.EqualTo(60));

        // Header
        ushort filenameLength = MemoryMarshal.Read<ushort>(span[..2]);
        Assert.That(filenameLength, Is.EqualTo(info.FileName.Length));
        ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
        Assert.That(generatedByLength, Is.EqualTo(info.GeneratedBy.Length));
        uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
        Assert.That(numFilaments, Is.EqualTo(0));
        uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
        Assert.That(fileSize, Is.EqualTo(4180));
        uint numLayers = MemoryMarshal.Read<uint>(span.Slice(20, 4));
        Assert.That(numLayers, Is.EqualTo(0));
        float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
        Assert.That(layerHeight, Is.EqualTo(0).Within(0.00001));
        float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
        Assert.That(objectHeight, Is.EqualTo(0).Within(0.00001));
        uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
        Assert.That(printTime, Is.EqualTo(0));
        uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
        Assert.That(simulatedTime, Is.EqualTo(0));

        // File name
        string fileName = Encoding.UTF8.GetString(span.Slice(40, info.FileName.Length));
        Assert.That(fileName, Is.EqualTo(info.FileName));

        // Generated by
        string generatedBy = Encoding.UTF8.GetString(span.Slice(40 + info.FileName.Length, generatedByLength));
        Assert.That(generatedBy, Is.EqualTo(info.GeneratedBy));
    }

    [Test]
    public void PrintStopped()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WritePrintStopped(span, PrintStoppedReason.Abort);
        Assert.That(bytesWritten, Is.EqualTo(4));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)PrintStoppedReason.Abort));

        // Padding
        Assert.That(span[1], Is.EqualTo(0));
        Assert.That(span[2], Is.EqualTo(0));
        Assert.That(span[3], Is.EqualTo(0));
    }

    [Test]
    public void MacroCompleted()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteMacroCompleted(span, DuetAPI.CodeChannel.File, false);
        Assert.That(bytesWritten, Is.EqualTo(4));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.File));
        Assert.That(span[1], Is.EqualTo(0));

        // Padding
        Assert.That(span[2], Is.EqualTo(0));
        Assert.That(span[3], Is.EqualTo(0));
    }

    [Test]
    public void CodeChannel()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteCodeChannel(span, DuetAPI.CodeChannel.LCD);
        Assert.That(bytesWritten, Is.EqualTo(4));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.LCD));

        // Padding
        Assert.That(span[1], Is.EqualTo(0));
        Assert.That(span[2], Is.EqualTo(0));
        Assert.That(span[3], Is.EqualTo(0));
    }

    [Test]
    public void AssignFilament()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteAssignFilament(span, 12, "foo bar");
        Assert.That(bytesWritten, Is.EqualTo(16));

        // Header
        int extruder = MemoryMarshal.Read<int>(span[..4]);
        Assert.That(extruder, Is.EqualTo(12));
        int filamentLength = MemoryMarshal.Read<int>(span.Slice(4, 4));
        Assert.That(filamentLength, Is.EqualTo(7));

        // Filament name
        string filamentName = Encoding.UTF8.GetString(span.Slice(8, 7));
        Assert.That(filamentName, Is.EqualTo("foo bar"));
    }

    [Test]
    public void EvaluateExpression()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteEvaluateExpression(span, DuetAPI.CodeChannel.SBC, "test expression");
        Assert.That(bytesWritten, Is.EqualTo(20));

        // Header
        Assert.That(span[0], Is.EqualTo((byte)DuetAPI.CodeChannel.SBC));
        Assert.That(span[1], Is.EqualTo(0));
        Assert.That(span[2], Is.EqualTo(0));
        Assert.That(span[3], Is.EqualTo(0));

        // Expression
        string expression = Encoding.UTF8.GetString(span.Slice(4, 15));
        Assert.That(expression, Is.EqualTo("test expression"));

        // Padding
        Assert.That(span[19], Is.EqualTo(0));
    }

    [Test]
    public void Message()
    {
        Span<byte> span = new byte[128];
        span.Fill(0xFF);

        int bytesWritten = Writer.WriteMessage(span, (MessageTypeFlags)(1 << (int)DuetAPI.CodeChannel.USB), "test\n");
        Assert.That(bytesWritten, Is.EqualTo(16));

        // Header
        uint messageFlags = MemoryMarshal.Read<uint>(span);
        Assert.That((MessageTypeFlags)messageFlags, Is.EqualTo((MessageTypeFlags)(1 << (int)DuetAPI.CodeChannel.USB)));
        uint messageLength = MemoryMarshal.Read<uint>(span[4..]);
        Assert.That(messageLength, Is.EqualTo(5));

        // Message
        string message = Encoding.UTF8.GetString(span.Slice(8, 5));
        Assert.That(message, Is.EqualTo("test\n"));

        // Padding
        Assert.That(span[13], Is.EqualTo(0));
        Assert.That(span[14], Is.EqualTo(0));
        Assert.That(span[15], Is.EqualTo(0));
    }
}
