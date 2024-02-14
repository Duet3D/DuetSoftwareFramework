using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI.ObjectModel;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.SbcRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.SPI.Serialization;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Code = DuetControlServer.Commands.Code;
using CodeFlags = DuetControlServer.SPI.Communication.SbcRequests.CodeFlags;

namespace UnitTests.SPI
{
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
            ClassicAssert.AreEqual(Consts.FormatCode, header.FormatCode);
            ClassicAssert.AreEqual(0, header.NumPackets);
            ClassicAssert.AreEqual(Consts.ProtocolVersion, header.ProtocolVersion);
            ClassicAssert.AreEqual(0, header.SequenceNumber);
            ClassicAssert.AreEqual(0, header.DataLength);
            ClassicAssert.AreEqual(0, header.ChecksumData32);
            ClassicAssert.AreEqual(0, header.ChecksumHeader32);

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
            ClassicAssert.AreEqual((ushort)Request.Reset, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            ClassicAssert.AreEqual(12, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            ClassicAssert.AreEqual(1054, packetLength);

            // Padding
            ClassicAssert.AreEqual(0, span[6]);
            ClassicAssert.AreEqual(0, span[7]);
        }

        [Test]
        public void SimpleCode()
        {
            Span<byte> span = new byte[128];

            Code code = new("G53 G10")
            {
                Channel = DuetAPI.CodeChannel.HTTP
            };

            int bytesWritten = Writer.WriteCode(span, code);
            ClassicAssert.AreEqual(16, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DuetAPI.CodeChannel.HTTP, span[0]);
            ClassicAssert.AreEqual((byte)(CodeFlags.HasMajorCommandNumber | CodeFlags.EnforceAbsolutePosition), span[1]);
            ClassicAssert.AreEqual(0, span[2]);                    // Number of parameters
            byte codeLetter = (byte)'G';
            ClassicAssert.AreEqual(codeLetter, span[3]);
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            ClassicAssert.AreEqual(10, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            ClassicAssert.AreEqual(-1, minorCode);
            uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
            ClassicAssert.AreEqual(0xFFFFFFFF, filePosition);

            // No padding
        }

        [Test]
        public void CodeWithParameters()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            Code code = new("G1 X4 Y23.5 Z12.2 J\"testok\" E12:3.45:5.67")
            {
                Channel = DuetAPI.CodeChannel.File
            };

            int bytesWritten = Writer.WriteCode(span, code);
            ClassicAssert.AreEqual(76, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DuetAPI.CodeChannel.File, span[0]);
            ClassicAssert.AreEqual((byte)CodeFlags.HasMajorCommandNumber, span[1]);
            ClassicAssert.AreEqual(5, span[2]);                    // Number of parameters
            ClassicAssert.AreEqual((byte)'G', span[3]);            // Code letter
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            ClassicAssert.AreEqual(1, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            ClassicAssert.AreEqual(-1, minorCode);
            uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
            ClassicAssert.AreEqual(0xFFFFFFFF, filePosition);

            // First parameter (X4)
            ClassicAssert.AreEqual((byte)'X', span[16]);
            ClassicAssert.AreEqual((byte)DataType.Int, span[17]);
            int intValue = MemoryMarshal.Read<int>(span.Slice(20, 4));
            ClassicAssert.AreEqual(4, intValue);

            // Second parameter (Y23.5)
            ClassicAssert.AreEqual((byte)'Y', span[24]);
            ClassicAssert.AreEqual((byte)DataType.Float, span[25]);
            float floatValue = MemoryMarshal.Read<float>(span.Slice(28, 4));
            ClassicAssert.AreEqual(23.5, floatValue, 0.00001);

            // Third parameter (Z12.2)
            ClassicAssert.AreEqual((byte)'Z', span[32]);
            ClassicAssert.AreEqual((byte)DataType.Float, span[33]);
            floatValue = MemoryMarshal.Read<float>(span.Slice(36, 4));
            ClassicAssert.AreEqual(12.2, floatValue, 0.00001);

            // Fourth parameter (J"testok")
            ClassicAssert.AreEqual((byte)'J', span[40]);
            ClassicAssert.AreEqual((byte)DataType.String, span[41]);
            intValue = MemoryMarshal.Read<int>(span.Slice(44, 4));
            ClassicAssert.AreEqual(6, intValue);

            // Fifth parameter (E12:3.45:5.67)
            ClassicAssert.AreEqual((byte)'E', span[48]);
            ClassicAssert.AreEqual((byte)DataType.FloatArray, span[49]);
            intValue = MemoryMarshal.Read<int>(span.Slice(52, 4));
            ClassicAssert.AreEqual(3, intValue);

            // Payload of fourth parameter ("test")
            string stringValue = Encoding.UTF8.GetString(span.Slice(56, 6));
            ClassicAssert.AreEqual("testok", stringValue);
            ClassicAssert.AreEqual(0, span[62]);
            ClassicAssert.AreEqual(0, span[63]);

            // Payload of fifth parameter (12:3.45:5.67)
            floatValue = MemoryMarshal.Read<float>(span.Slice(64, 4));
            ClassicAssert.AreEqual(12, floatValue, 0.00001);
            floatValue = MemoryMarshal.Read<float>(span.Slice(68, 4));
            ClassicAssert.AreEqual(3.45, floatValue, 0.00001);
            floatValue = MemoryMarshal.Read<float>(span.Slice(72, 4));
            ClassicAssert.AreEqual(5.67, floatValue, 0.00001);
        }

        [Test]
        public void Comment()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            Code code = new("; Hello world")
            {
                Channel = DuetAPI.CodeChannel.Telnet
            };

            int bytesWritten = Writer.WriteCode(span, code);
            ClassicAssert.AreEqual(36, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DuetAPI.CodeChannel.Telnet, span[0]);
            ClassicAssert.AreEqual((byte)CodeFlags.HasMajorCommandNumber, span[1]);
            ClassicAssert.AreEqual(1, span[2]);                    // Number of parameters
            ClassicAssert.AreEqual((byte)'Q', span[3]);            // Code letter
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            ClassicAssert.AreEqual(0, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            ClassicAssert.AreEqual(-1, minorCode);
            uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
            ClassicAssert.AreEqual(0xFFFFFFFF, filePosition);

            // Comment parameter
            ClassicAssert.AreEqual((byte)'@', span[16]);
            ClassicAssert.AreEqual((byte)DataType.String, span[17]);
            int intValue = MemoryMarshal.Read<int>(span.Slice(20, 4));
            ClassicAssert.AreEqual(11, intValue);

            // Comment payload ("Hello world")
            string stringValue = Encoding.UTF8.GetString(span.Slice(24, 11));
            ClassicAssert.AreEqual("Hello world", stringValue);
            ClassicAssert.AreEqual(0, span[35]);
        }

        [Test]
        public void GetObjectModel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteGetObjectModel(span, "move", "d99vn");
            ClassicAssert.AreEqual(16, bytesWritten);

            // Header
            ClassicAssert.AreEqual(4, MemoryMarshal.Read<ushort>(span));               // Key length
            ClassicAssert.AreEqual(5, MemoryMarshal.Read<ushort>(span.Slice(2, 2)));   // Flags length

            // Key
            string key = Encoding.UTF8.GetString(span.Slice(4, 4));
            ClassicAssert.AreEqual("move", key);

            // Flags
            string flags = Encoding.UTF8.GetString(span.Slice(8, 5));
            ClassicAssert.AreEqual("d99vn", flags);

            // Padding
            ClassicAssert.AreEqual(0, span[13]);
            ClassicAssert.AreEqual(0, span[14]);
            ClassicAssert.AreEqual(0, span[15]);
        }

        [Test]
        public void SetObjectModel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteSetObjectModel(span, "foobar", "myval");
            ClassicAssert.AreEqual(24, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DataType.String, span[0]);
            ClassicAssert.AreEqual(6, span[1]);                        // Field path length
            int intValue = MemoryMarshal.Read<int>(span.Slice(4, 4));
            ClassicAssert.AreEqual(5, intValue);

            // Field path
            string field = Encoding.UTF8.GetString(span.Slice(8, 6));
            ClassicAssert.AreEqual("foobar", field);
            ClassicAssert.AreEqual(0, span[14]);
            ClassicAssert.AreEqual(0, span[15]);

            // Field value
            string value = Encoding.UTF8.GetString(span.Slice(16, 5));
            ClassicAssert.AreEqual("myval", value);

            // Padding
            ClassicAssert.AreEqual(0, span[21]);
            ClassicAssert.AreEqual(0, span[22]);
            ClassicAssert.AreEqual(0, span[23]);
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
            ClassicAssert.AreEqual(72, bytesWritten);

            // Header
            ushort filenameLength = MemoryMarshal.Read<ushort>(span[..2]);
            ClassicAssert.AreEqual(info.FileName.Length, filenameLength);
            ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            ClassicAssert.AreEqual(6, generatedByLength);
            uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
            ClassicAssert.AreEqual(2, numFilaments);
            uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
            ClassicAssert.AreEqual(452432, fileSize);
            uint numLayers = MemoryMarshal.Read<uint>(span.Slice(20, 4));
            ClassicAssert.AreEqual(16, numLayers);
            float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
            ClassicAssert.AreEqual(0.2, layerHeight, 0.00001);
            float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
            ClassicAssert.AreEqual(53.4, objectHeight, 0.00001);
            uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
            ClassicAssert.AreEqual(12355, printTime);
            uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
            ClassicAssert.AreEqual(10323, simulatedTime);

            // Filament consumption
            float filamentUsageA = MemoryMarshal.Read<float>(span.Slice(40, 4));
            ClassicAssert.AreEqual(123.45, filamentUsageA, 0.0001);
            float filamentUsageB = MemoryMarshal.Read<float>(span.Slice(44, 4));
            ClassicAssert.AreEqual(678.9, filamentUsageB, 0.0001);

            // File name
            string fileName = Encoding.UTF8.GetString(span.Slice(48, info.FileName.Length));
            ClassicAssert.AreEqual(info.FileName, fileName);

            // Generated by
            string generatedBy = Encoding.UTF8.GetString(span.Slice(48 + info.FileName.Length, info.GeneratedBy.Length));
            ClassicAssert.AreEqual(info.GeneratedBy, generatedBy);
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
            ClassicAssert.AreEqual(60, bytesWritten);

            // Header
            ushort filenameLength = MemoryMarshal.Read<ushort>(span[..2]);
            ClassicAssert.AreEqual(info.FileName.Length, filenameLength);
            ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            ClassicAssert.AreEqual(info.GeneratedBy.Length, generatedByLength);
            uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
            ClassicAssert.AreEqual(0, numFilaments);
            uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
            ClassicAssert.AreEqual(4180, fileSize);
            uint numLayers = MemoryMarshal.Read<uint>(span.Slice(20, 4));
            ClassicAssert.AreEqual(0, numLayers);
            float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
            ClassicAssert.AreEqual(0, layerHeight, 0.00001);
            float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
            ClassicAssert.AreEqual(0, objectHeight, 0.00001);
            uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
            ClassicAssert.AreEqual(0, printTime);
            uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
            ClassicAssert.AreEqual(0, simulatedTime);

            // File name
            string fileName = Encoding.UTF8.GetString(span.Slice(40, info.FileName.Length));
            ClassicAssert.AreEqual(info.FileName, fileName);

            // Generated by
            string generatedBy = Encoding.UTF8.GetString(span.Slice(40 + info.FileName.Length, generatedByLength));
            ClassicAssert.AreEqual(info.GeneratedBy, generatedBy);
        }

        [Test]
        public void PrintStopped()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WritePrintStopped(span, PrintStoppedReason.Abort);
            ClassicAssert.AreEqual(bytesWritten, 4);

            // Header
            ClassicAssert.AreEqual((byte)PrintStoppedReason.Abort, span[0]);

            // Padding
            ClassicAssert.AreEqual(0, span[1]);
            ClassicAssert.AreEqual(0, span[2]);
            ClassicAssert.AreEqual(0, span[3]);
        }

        [Test]
        public void MacroCompleted()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteMacroCompleted(span, DuetAPI.CodeChannel.File, false);
            ClassicAssert.AreEqual(4, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DuetAPI.CodeChannel.File, span[0]);
            ClassicAssert.AreEqual(0, span[1]);

            // Padding
            ClassicAssert.AreEqual(0, span[2]);
            ClassicAssert.AreEqual(0, span[3]);
        }

        [Test]
        public void CodeChannel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteCodeChannel(span, DuetAPI.CodeChannel.LCD);
            ClassicAssert.AreEqual(4, bytesWritten);

            // Header
            ClassicAssert.AreEqual(span[0], (byte)DuetAPI.CodeChannel.LCD);

            // Padding
            ClassicAssert.AreEqual(0, span[1]);
            ClassicAssert.AreEqual(0, span[2]);
            ClassicAssert.AreEqual(0, span[3]);
        }

        [Test]
        public void AssignFilament()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteAssignFilament(span, 12, "foo bar");
            ClassicAssert.AreEqual(16, bytesWritten);

            // Header
            int extruder = MemoryMarshal.Read<int>(span[..4]);
            ClassicAssert.AreEqual(12, extruder);
            int filamentLength = MemoryMarshal.Read<int>(span.Slice(4, 4));
            ClassicAssert.AreEqual(7, filamentLength);

            // Filament name
            string filamentName = Encoding.UTF8.GetString(span.Slice(8, 7));
            ClassicAssert.AreEqual("foo bar", filamentName);
        }

        [Test]
        public void EvaluateExpression()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteEvaluateExpression(span, DuetAPI.CodeChannel.SBC, "test expression");
            ClassicAssert.AreEqual(20, bytesWritten);

            // Header
            ClassicAssert.AreEqual((byte)DuetAPI.CodeChannel.SBC, span[0]);
            ClassicAssert.AreEqual(0, span[1]);
            ClassicAssert.AreEqual(0, span[2]);
            ClassicAssert.AreEqual(0, span[3]);

            // Expression
            string expression = Encoding.UTF8.GetString(span.Slice(4, 15));
            ClassicAssert.AreEqual(expression, "test expression");

            // Padding
            ClassicAssert.AreEqual(0, span[19]);
        }

        [Test]
        public void Message()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteMessage(span, (MessageTypeFlags)(1 << (int)DuetAPI.CodeChannel.USB), "test\n");
            ClassicAssert.AreEqual(16, bytesWritten);

            // Header
            uint messageFlags = MemoryMarshal.Read<uint>(span);
            ClassicAssert.AreEqual((MessageTypeFlags)(1 << (int)DuetAPI.CodeChannel.USB), (MessageTypeFlags)messageFlags);
            uint messageLength = MemoryMarshal.Read<uint>(span[4..]);
            ClassicAssert.AreEqual(5, messageLength);

            // Message
            string message = Encoding.UTF8.GetString(span.Slice(8, 5));
            ClassicAssert.AreEqual(message, "test\n");

            // Padding
            ClassicAssert.AreEqual(0, span[13]);
            ClassicAssert.AreEqual(0, span[14]);
            ClassicAssert.AreEqual(0, span[15]);
        }
    }
}