using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Utility;
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
            span.Fill(0xFF);

            TransferHeader header = MemoryMarshal.Read<TransferHeader>(span);
            Writer.InitTransferHeader(ref header);
            
            // Header
            Assert.AreEqual(Consts.FormatCode, header.FormatCode);
            Assert.AreEqual(0, header.NumPackets);
            Assert.AreEqual(Consts.ProtocolVersion, header.ProtocolVersion);
            Assert.AreEqual(0, header.SequenceNumber);
            Assert.AreEqual(0, header.DataLength);
            Assert.AreEqual(0, header.ChecksumData);
            Assert.AreEqual(0, header.ChecksumHeader);
            
            // No padding
        }
        
        [Test]
        public void PacketHeader()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            Writer.WritePacketHeader(span, Request.Reset, 12, 1054);
            
            // Header
            ushort request = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual((ushort)Request.Reset, request);
            ushort packetId = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(12, packetId);
            ushort packetLength = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            Assert.AreEqual(1054, packetLength);

            // Padding
            Assert.AreEqual(0, span[6]);
            Assert.AreEqual(0, span[7]);
        }

        [Test]
        public void SimpleCode()
        {
            Span<byte> span = new byte[128];

            Code code = new Code("G53 G10")
            {
                Channel = CodeChannel.HTTP
            };

            int bytesWritten = Writer.WriteCode(span, code);
            Assert.AreEqual(16, bytesWritten);

            // Header
            Assert.AreEqual((byte)CodeChannel.HTTP, span[0]);
            Assert.AreEqual((byte)(SpiCodeFlags.HasMajorCommandNumber | SpiCodeFlags.EnforceAbsolutePosition), span[1]);
            Assert.AreEqual(0, span[2]);                    // Number of parameters
            byte codeLetter = (byte)'G';
            Assert.AreEqual(codeLetter, span[3]);
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            Assert.AreEqual(10, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            Assert.AreEqual(-1, minorCode);
            uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
            Assert.AreEqual(0xFFFFFFFF, filePosition);
            
            // No padding
        }

        [Test]
        public void CodeWithParameters()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            Code code = new Code("G1 X4 Y23.5 Z12.2 J\"testok\" E12:3.45")
            {
                Channel = CodeChannel.File
            };

            int bytesWritten = Writer.WriteCode(span, code);
            Assert.AreEqual(72, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)CodeChannel.File, span[0]);
            Assert.AreEqual((byte)SpiCodeFlags.HasMajorCommandNumber, span[1]);
            Assert.AreEqual(5, span[2]);                    // Number of parameters
            Assert.AreEqual((byte)'G', span[3]);            // Code letter
            int majorCode = MemoryMarshal.Read<int>(span.Slice(4, 4));
            Assert.AreEqual(1, majorCode);
            int minorCode = MemoryMarshal.Read<int>(span.Slice(8, 4));
            Assert.AreEqual(-1, minorCode);
            uint filePosition = MemoryMarshal.Read<uint>(span.Slice(12, 4));
            Assert.AreEqual(0xFFFFFFFF, filePosition);
            
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
            
            // Fourth parameter (J"testok")
            Assert.AreEqual((byte)'J', span[40]);
            Assert.AreEqual((byte)DataType.String, span[41]);
            intValue = MemoryMarshal.Read<int>(span.Slice(44, 4));
            Assert.AreEqual(6, intValue);

            // Fifth parameter (E12:3.45)
            Assert.AreEqual((byte)'E', span[48]);
            Assert.AreEqual((byte)DataType.FloatArray, span[49]);
            intValue = MemoryMarshal.Read<int>(span.Slice(52, 4));
            Assert.AreEqual(2, intValue);
            
            // Payload of fourth parameter ("test")
            string stringValue = Encoding.UTF8.GetString(span.Slice(56, 6));
            Assert.AreEqual("testok", stringValue);
            Assert.AreEqual(0, span[62]);
            Assert.AreEqual(0, span[63]);
            
            // Payload of fifth parameter (12:3.45)
            floatValue = MemoryMarshal.Read<float>(span.Slice(64, 4));
            Assert.AreEqual(12, floatValue, 0.00001);
            floatValue = MemoryMarshal.Read<float>(span.Slice(68, 4));
            Assert.AreEqual(3.45, floatValue, 0.00001);
        }
        
        [Test]
        public void GetObjectModel()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            int bytesWritten = Writer.WriteObjectModelRequest(span, 5);
            Assert.AreEqual(4, bytesWritten);

            // Header
            Assert.AreEqual(0, MemoryMarshal.Read<ushort>(span));   // Length
            Assert.AreEqual(5, span[2]);                            // Module

            // Padding
            Assert.AreEqual(0, span[3]);
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
            Assert.AreEqual(0, span[15]);
            
            // Field value
            string value = Encoding.UTF8.GetString(span.Slice(16, 5));
            Assert.AreEqual("myval", value);
            Assert.AreEqual(0, span[21]);
            Assert.AreEqual(0, span[22]);
            Assert.AreEqual(0, span[23]);
        }
        
        [Test]
        public void PrintStarted()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            ParsedFileInfo info = new ParsedFileInfo
            {
                Size = 452432,
                FileName = "0:/gcodes/test.g",
                FirstLayerHeight = 0.3F,
                GeneratedBy = "Slic3r",
                Height = 53.4F,
                LastModified = new DateTime(2014, 11, 23),
                NumLayers = 343,
                LayerHeight = 0.2F,
                PrintTime = 12355,
                SimulatedTime = 10323
            };
            info.Filament.Add(123.45F);
            info.Filament.Add(678.9F);
            
            int bytesWritten = Writer.WritePrintStarted(span, info);
            Assert.AreEqual(72, bytesWritten);

            // Header
            ushort filenameLength = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual(info.FileName.Length, filenameLength);
            ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(6, generatedByLength);
            uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
            Assert.AreEqual(2, numFilaments);
            ulong expectedModifiedDate = (ulong)(info.LastModified.Value - new DateTime (1970, 1, 1)).TotalSeconds;
            ulong modifiedDate = MemoryMarshal.Read<ulong>(span.Slice(8, 8));
            Assert.AreEqual(expectedModifiedDate, modifiedDate);
            uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
            Assert.AreEqual(452432, fileSize);
            float firstLayerHeight = MemoryMarshal.Read<float>(span.Slice(20, 4));
            Assert.AreEqual(0.3, firstLayerHeight, 0.00001);
            float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
            Assert.AreEqual(0.2, layerHeight, 0.00001);
            float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
            Assert.AreEqual(53.4, objectHeight, 0.00001);
            uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
            Assert.AreEqual(12355, printTime);
            uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
            Assert.AreEqual(10323, simulatedTime);

            // Filament consumption
            float filamentUsageA = MemoryMarshal.Read<float>(span.Slice(40, 4));
            Assert.AreEqual(123.45, filamentUsageA, 0.0001);
            float filamentUsageB = MemoryMarshal.Read<float>(span.Slice(44, 4));
            Assert.AreEqual(678.9, filamentUsageB, 0.0001);
            
            // File name
            string fileName = Encoding.UTF8.GetString(span.Slice(48, info.FileName.Length));
            Assert.AreEqual(info.FileName, fileName);
            
            // Generated by
            string generatedBy = Encoding.UTF8.GetString(span.Slice(48 + info.FileName.Length, info.GeneratedBy.Length));
            Assert.AreEqual(info.GeneratedBy, generatedBy);
        }

        [Test]
        public void PrintStarted2()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            ParsedFileInfo info = new ParsedFileInfo
            {
                Size = 4180,
                FileName = "0:/gcodes/circle.g",
                FirstLayerHeight = 0.5F,
                GeneratedBy = "",
                Height = 0,
                LastModified = new DateTime(2019, 4, 23),
                NumLayers = null,
                LayerHeight = 0,
                PrintTime = 0,
                SimulatedTime = 0,
            };

            int bytesWritten = Writer.WritePrintStarted(span, info);
            Assert.AreEqual(60, bytesWritten);

            // Header
            ushort filenameLength = MemoryMarshal.Read<ushort>(span.Slice(0, 2));
            Assert.AreEqual(info.FileName.Length, filenameLength);
            ushort generatedByLength = MemoryMarshal.Read<ushort>(span.Slice(2, 2));
            Assert.AreEqual(info.GeneratedBy.Length, generatedByLength);
            uint numFilaments = MemoryMarshal.Read<uint>(span.Slice(4, 4));
            Assert.AreEqual(0, numFilaments);
            ulong expectedModifiedDate = (ulong)(info.LastModified.Value - new DateTime(1970, 1, 1)).TotalSeconds;
            ulong modifiedDate = MemoryMarshal.Read<ulong>(span.Slice(8, 8));
            Assert.AreEqual(expectedModifiedDate, modifiedDate);
            uint fileSize = MemoryMarshal.Read<uint>(span.Slice(16, 4));
            Assert.AreEqual(4180, fileSize);
            float firstLayerHeight = MemoryMarshal.Read<float>(span.Slice(20, 4));
            Assert.AreEqual(0.5, firstLayerHeight, 0.00001);
            float layerHeight = MemoryMarshal.Read<float>(span.Slice(24, 4));
            Assert.AreEqual(0, layerHeight, 0.00001);
            float objectHeight = MemoryMarshal.Read<float>(span.Slice(28, 4));
            Assert.AreEqual(0, objectHeight, 0.00001);
            uint printTime = MemoryMarshal.Read<uint>(span.Slice(32, 4));
            Assert.AreEqual(0, printTime);
            uint simulatedTime = MemoryMarshal.Read<uint>(span.Slice(36, 4));
            Assert.AreEqual(0, simulatedTime);

            // File name
            string fileName = Encoding.UTF8.GetString(span.Slice(40, info.FileName.Length));
            Assert.AreEqual(info.FileName, fileName);

            // Generated by
            string generatedBy = Encoding.UTF8.GetString(span.Slice(40 + info.FileName.Length, generatedByLength));
            Assert.AreEqual(info.GeneratedBy, generatedBy);
        }

        [Test]
        public void PrintStopped()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WritePrintStopped(span, PrintStoppedReason.Abort);
            Assert.AreEqual(bytesWritten, 4);

            // Header
            Assert.AreEqual((byte)PrintStoppedReason.Abort, span[0]);

            // Padding
            Assert.AreEqual(0, span[1]);
            Assert.AreEqual(0, span[2]);
            Assert.AreEqual(0, span[3]);
        }

        [Test]
        public void MacroCompleted()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);
            
            int bytesWritten = Writer.WriteMacroCompleted(span, CodeChannel.File, false);
            Assert.AreEqual(4, bytesWritten);
            
            // Header
            Assert.AreEqual((byte)CodeChannel.File, span[0]);
            Assert.AreEqual(0, span[1]);
            
            // Padding
            Assert.AreEqual(0, span[2]);
            Assert.AreEqual(0, span[3]);
        }


        [Test]
        public void Heightmap()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            Heightmap map = new Heightmap
            {
                XMin = 20,
                XMax = 180,
                XSpacing = 40,
                YMin = 50,
                YMax = 150,
                YSpacing = 50,
                Radius = 0,
                NumX = 3,
                NumY = 4,
                ZCoordinates = new float[] {
                    10, 20, 30,
                    40, 50, 60,
                    70, 80, 90,
                    100, 110, 120
                }
            };

            int bytesWritten = Writer.WriteHeightMap(span, map);
            Assert.AreEqual(80, bytesWritten);

            // Header
            float xMin = MemoryMarshal.Read<float>(span);
            Assert.AreEqual(20, xMin, 0.0001);
            float xMax = MemoryMarshal.Read<float>(span.Slice(4, 4));
            Assert.AreEqual(180, xMax, 0.0001);
            float xSpacing = MemoryMarshal.Read<float>(span.Slice(8, 4));
            Assert.AreEqual(40, xSpacing, 0.0001);
            float yMin = MemoryMarshal.Read<float>(span.Slice(12, 4));
            Assert.AreEqual(50, yMin, 0.0001);
            float yMax = MemoryMarshal.Read<float>(span.Slice(16, 4));
            Assert.AreEqual(150, yMax, 0.0001);
            float ySpacing = MemoryMarshal.Read<float>(span.Slice(20, 4));
            Assert.AreEqual(50, ySpacing, 0.0001);
            float radius = MemoryMarshal.Read<float>(span.Slice(24, 4));
            Assert.AreEqual(0, radius, 0.0001);
            ushort numX = MemoryMarshal.Read<ushort>(span.Slice(28, 2));
            Assert.AreEqual(3, numX);
            ushort numY = MemoryMarshal.Read<ushort>(span.Slice(30, 2));
            Assert.AreEqual(4, numY);

            // Points
            Span<float> zCoordinates = MemoryMarshal.Cast<byte, float>(span.Slice(32));
            for (int i = 0; i < map.ZCoordinates.Length; i++)
            {
                Assert.AreEqual(zCoordinates[i], 10 * i + 10, 0.0001);
            }
        }

        [Test]
        public void LockUnlock()
        {
            Span<byte> span = new byte[128];
            span.Fill(0xFF);

            int bytesWritten = Writer.WriteLockUnlock(span, CodeChannel.LCD);
            Assert.AreEqual(4, bytesWritten);

            // Header
            Assert.AreEqual(span[0], (byte)CodeChannel.LCD);

            // Padding
            Assert.AreEqual(0, span[1]);
            Assert.AreEqual(0, span[2]);
            Assert.AreEqual(0, span[3]);
        }
    }
}