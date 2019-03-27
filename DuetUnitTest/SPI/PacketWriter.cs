using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.LinuxRequests;
using DuetControlServer.SPI.Serialization;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace DuetUnitTest.SPI
{
    [TestFixture]
    public class PacketWriter
    {
        [Test]
        public void TransferHeader()
        {
            Span<byte> span = new byte[128];
            
            Writer.WriteTransferHeader(span, 4, 1234, 456, 0);
            
            // Header
            Assert.AreEqual(Consts.FormatCode, span[0]);
            ushort protocolVersion = MemoryMarshal.Read<ushort>(span.Slice(1, 2));
            Assert.AreEqual(Consts.ProtocolVersion, protocolVersion);
            Assert.AreEqual(4, span[3]);        // Number of packets
            uint sequenceNumber = MemoryMarshal.Read<uint>(span.Slice(4, 4));
            Assert.AreEqual(1234, sequenceNumber);
            ushort transferLength = MemoryMarshal.Read<ushort>(span.Slice(8, 2));
            Assert.AreEqual(456, transferLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(10, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void EmergencyStop()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.EmergencyStop, 123, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.EmergencyStop, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(123, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void Reset()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.Reset, 1054, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.Reset, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(1054, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void CodeHeader()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.Code, 234, 54);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.Code, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(234, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(54, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum); 
            
            // No padding
        }
        
        [Test]
        public void SimpleCode()
        {
            Span<byte> span = new byte[128];

            Code code = new Code("G53 G10")
            {
                Source = CodeChannel.HTTP
            };

            int bytesWritten = Writer.WriteCode(span, code);
            Assert.AreEqual(16, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)CodeChannel.HTTP, span[0]);
            Assert.AreEqual((byte)CodeFlags.EnforceAbsolutePosition, span[1]);
            Assert.AreEqual(0, span[2]);                    // Number of parameters
            byte codeLetter = (byte)'G';
            Assert.AreEqual(codeLetter, span[3]);
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            Assert.AreEqual(10, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            Assert.AreEqual(-1, minorCode);
            int filePosition = MemoryMarshal.Read<int>(span.Slice(12, 4));
            Assert.AreEqual(0, filePosition);
            
            // No padding
        }

        [Test]
        public void CodeWithParameters()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            Code code = new Code("G1 X4 Y23.5 Z12.2 E12:3.45 J\"test\"")
            {
                Source = CodeChannel.File
            };

            int bytesWritten = Writer.WriteCode(span, code);
            Assert.AreEqual(72, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)CodeChannel.File, span[0]);
            Assert.AreEqual(0, span[1]);                    // Flags
            Assert.AreEqual(5, span[2]);                    // Number of parameters
            Assert.AreEqual((byte)'G', span[3]);            // Code letter
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            Assert.AreEqual(1, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            Assert.AreEqual(-1, minorCode);
            int filePosition = MemoryMarshal.Read<int>(span.Slice(12, 4));
            Assert.AreEqual(0, filePosition);
            
            // First parameter (X4)
            Assert.AreEqual((byte)'X', span[16]);
            Assert.AreEqual((byte)DataType.Int, span[17]);
            int intValue = MemoryMarshal.Read<int>(span.Slice(20, 4));
            Assert.AreEqual(4, intValue);
            
            // Second parameter (Y23.5)
            Assert.AreEqual((byte)'Y', span[24]);
            Assert.AreEqual((byte)DataType.Float, span[25]);
            float floatValue = MemoryMarshal.Read<float>(span.Slice(28, 4));
            Assert.AreEqual(23.5, floatValue, 0.00001);
            
            // Third parameter (Z12.2)
            Assert.AreEqual((byte)'Z', span[32]);
            Assert.AreEqual((byte)DataType.Float, span[33]);
            floatValue = MemoryMarshal.Read<float>(span.Slice(36, 4));
            Assert.AreEqual(12.2, floatValue, 0.00001);
            
            // Fourth parameter (E12:3.45)
            Assert.AreEqual((byte)'E', span[40]);
            Assert.AreEqual((byte)DataType.FloatArray, span[41]);
            intValue = MemoryMarshal.Read<int>(span.Slice(44, 4));
            Assert.AreEqual(2, intValue);
            
            // Fifth parameter (J"test")
            Assert.AreEqual((byte)'J', span[48]);
            Assert.AreEqual((byte)DataType.String, span[49]);
            intValue = MemoryMarshal.Read<int>(span.Slice(52, 4));
            Assert.AreEqual(4, intValue);
            
            // Payload of fourth parameter (12:3.45)
            floatValue = MemoryMarshal.Read<float>(span.Slice(56, 4));
            Assert.AreEqual(12, floatValue, 0.00001);
            floatValue = MemoryMarshal.Read<float>(span.Slice(60, 4));
            Assert.AreEqual(3.45, floatValue, 0.00001);
            
            // Payload of fifth parameter ("test")
            string stringValue = Encoding.UTF8.GetString(span.Slice(64, 4));
            Assert.AreEqual("test", stringValue);
            Assert.AreEqual(0, span[68]);
            
            // Padding
            Assert.AreEqual(0, span[69]);
            Assert.AreEqual(0, span[70]);
            Assert.AreEqual(0, span[71]);
        }
        
        [Test]
        public void GetObjectModelHeader()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.GetObjectModel, 462, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.GetObjectModel, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(462, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void GetObjectModel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            int bytesWritten = Writer.WriteObjectModelRequest(span, 5);
            Assert.AreEqual(4, bytesWritten);
            
            // Header
            Assert.AreEqual(5, span[0]);
            
            // Padding
            Assert.AreEqual(0, span[1]);
            Assert.AreEqual(0, span[2]);
            Assert.AreEqual(0, span[3]);
        }
        
        [Test]
        public void SetObjectModelHeader()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.SetObjectModel, 180, 123);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.SetObjectModel, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(180, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(123, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void SetObjectModel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            int bytesWritten = Writer.WriteObjectModel(span, "foobar", "myval");
            Assert.AreEqual(24, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)DataType.String, span[0]);
            Assert.AreEqual(6, span[1]);                        // Field path length
            int intValue = MemoryMarshal.Read<int>(span.Slice(4, 4));
            Assert.AreEqual(5, intValue);
            
            // Field path
            string field = Encoding.UTF8.GetString(span.Slice(8, 6));
            Assert.AreEqual("foobar", field);
            Assert.AreEqual(0, span[14]);
            
            // Field value
            string value = Encoding.UTF8.GetString(span.Slice(15, 5));
            Assert.AreEqual("myval", value);
            Assert.AreEqual(0, span[20]);
            
            // Padding
            Assert.AreEqual(0, span[21]);
            Assert.AreEqual(0, span[22]);
            Assert.AreEqual(0, span[23]);
        }
        
        [Test]
        public void SetFilePrintInfoHeader()
        {
            Span<byte> span = new byte[128];
            
            Writer.WritePacketHeader(span, Request.SetFilePrintInfo, 123, 456);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.SetFilePrintInfo, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(123, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(456, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void SetFilePrintInfo()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            ParsedFileInfo info = new ParsedFileInfo
            {
                Size = 452432,
                Filament = new [] { 123.45, 678.9 },
                FileName = "test.g",
                FirstLayerHeight = 0.3,
                GeneratedBy = "Slic3r",
                Height = 53.4,
                LastModified = new DateTime(2014, 11, 23),
                NumLayers = 343,
                LayerHeight = 0.2,
                PrintTime = 12354.6,
                SimulatedTime = 10323.4
            };
            
            int bytesWritten = Writer.WriteFilePrintInfo(span, info);
            Assert.AreEqual(60, bytesWritten);
            
            // Header
            uint fileSize = MemoryMarshal.Read<uint>(span.Slice(0, 4));
            Assert.AreEqual(452432, fileSize);
            ulong expectedModifiedDate = (ulong)(info.LastModified.Value - new DateTime (1970, 1, 1)).TotalSeconds;
            ulong modifiedDate = MemoryMarshal.Read<ulong>(span.Slice(4, 8));
            Assert.AreEqual(expectedModifiedDate, modifiedDate);
            float layerHeight = MemoryMarshal.Read<float>(span.Slice(12, 4));
            Assert.AreEqual(0.2, layerHeight, 0.00001);
            float firstLayerHeight = MemoryMarshal.Read<float>(span.Slice(16, 4));
            Assert.AreEqual(0.3, firstLayerHeight, 0.00001);
            float objectHeight = MemoryMarshal.Read<float>(span.Slice(20, 4));
            Assert.AreEqual(53.4, objectHeight, 0.00001);
            uint printTime = MemoryMarshal.Read<uint>(span.Slice(24, 4));
            Assert.AreEqual(12355, printTime);
            uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(28, 4));
            Assert.AreEqual(10323, simulatedTime);
            uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(32, 4));
            Assert.AreEqual(2, numFilaments);

            // Filament consumption
            float filamentUsageA = MemoryMarshal.Read<float>(span.Slice(36, 4));
            Assert.AreEqual(123.45, filamentUsageA, 0.0001);
            float filamentUsageB = MemoryMarshal.Read<float>(span.Slice(40, 4));
            Assert.AreEqual(678.9, filamentUsageB, 0.0001);
            
            // File name
            string fileName = Encoding.UTF8.GetString(span.Slice(44, 6));
            Assert.AreEqual("test.g", fileName);
            Assert.AreEqual(0, span[50]);
            
            // Generated by
            string generatedBy = Encoding.UTF8.GetString(span.Slice(51, 6));
            Assert.AreEqual("Slic3r", generatedBy);
            Assert.AreEqual(0, span[57]);
            
            // Padding
            Assert.AreEqual(0, span[58]);
            Assert.AreEqual(0, span[59]);
        }

        [Test]
        public void ResetFilePrintInfo()
        {
            Span<byte> span = new byte[128];
            Writer.WritePacketHeader(span, Request.ResetFilePrintInfo, 346, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.ResetFilePrintInfo, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(346, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
        
        [Test]
        public void MacroCompletedHeader()
        {
            Span<byte> span = new byte[128];
            Writer.WritePacketHeader(span, Request.MacroCompleted, 464, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.MacroCompleted, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(464, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }

        [Test]
        public void MacroCompleted()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            int bytesWritten = Writer.WriteMacroCompleted(span, CodeChannel.File);
            Assert.AreEqual(4, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)CodeChannel.File, span[0]);
            
            // Padding
            Assert.AreEqual(0, span[1]);
            Assert.AreEqual(0, span[2]);
            Assert.AreEqual(0, span[3]);
        }
        
        [Test]
        public void GetHeightMap()
        {
            Span<byte> span = new byte[128];
            Writer.WritePacketHeader(span, Request.GetHeightMap, 1953, 0);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.GetHeightMap, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(1953, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(0, packetLength);
            ushort checksum = MemoryMarshal.Read<ushort>(span.Slice(6, 2));
            Assert.AreEqual(0, checksum);
            
            // No padding
        }
    }
}