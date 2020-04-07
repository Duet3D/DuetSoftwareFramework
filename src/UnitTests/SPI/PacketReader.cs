using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.SPI.Serialization;
using NUnit.Framework;

namespace UnitTests.SPI
{
    [TestFixture]
    public class PacketReader
    {
        [Test]
        public void TransferHeader()
        {
            Span<byte> blob = GetBlob("transferHeader.bin");
            
            TransferHeader header = MemoryMarshal.Read<TransferHeader>(blob);
            
            // Header
            Assert.AreEqual(Consts.FormatCode, header.FormatCode);
            Assert.AreEqual(4, header.NumPackets);
            Assert.AreEqual(Consts.ProtocolVersion, header.ProtocolVersion);
            Assert.AreEqual(12345, header.SequenceNumber);
            Assert.AreEqual(1436, header.DataLength);
            Assert.AreEqual(0, header.ChecksumData);
            Assert.AreEqual(0, header.ChecksumHeader);
            
            // No padding
        }

        [Test]
        public void PacketHeader()
        {
            Span<byte> blob = GetBlob("packetHeader.bin");
            
            PacketHeader header = Reader.ReadPacketHeader(blob);
            
            // Header
            Assert.AreEqual((ushort)Request.ObjectModel, header.Request);
            Assert.AreEqual(12, header.Id);
            Assert.AreEqual(300, header.Length);
        }

        [Test]
        public void PacketHeaderResend()
        {
            Span<byte> blob = GetBlob("packetHeaderResend.bin");

            PacketHeader header = Reader.ReadPacketHeader(blob);

            // Header
            Assert.AreEqual((ushort)Request.ResendPacket, header.Request);
            Assert.AreEqual(23, header.Id);
            Assert.AreEqual(0, header.Length);
            Assert.AreEqual(12, header.ResendPacketId);
        }

        [Test]
        public void StringRequest()
        {
            Span<byte> blob = GetBlob("stringRequest.bin");
            
            int bytesRead = Reader.ReadStringRequest(blob, out ReadOnlySpan<byte> json);
            Assert.AreEqual(24, bytesRead);
            
            // JSON
            Assert.AreEqual("{\"hello\":\"json!\"}", Encoding.UTF8.GetString(json));
        }

        [Test]
        public void CodeBufferUpdate()
        {
            Span<byte> blob = GetBlob("codeBufferUpdate.bin");

            int bytesRead = Reader.ReadCodeBufferUpdate(blob, out ushort bufferSpace);
            Assert.AreEqual(4, bytesRead);

            // Header
            Assert.AreEqual(787, bufferSpace);
        }

        [Test]
        public void Message()
        {
            Span<byte> blob = GetBlob("message.bin");

            int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
            Assert.AreEqual(28, bytesRead);

            // Header
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.HttpMessage));
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.TelnetMessage));
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.UsbMessage));
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.AuxMessage));
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.WarningMessageFlag));
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.PushFlag));
            
            // Message
            Assert.AreEqual("This is just a test", reply);
        }
        
        [Test]
        public void EmptyMessage()
        {
            Span<byte> blob = GetBlob("emptyMessage.bin");

            int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
            Assert.AreEqual(8, bytesRead);

            // Header
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.UsbMessage));

            // Message
            Assert.IsEmpty(reply);
        }
        
        [Test]
        public void MacroRequest()
        {
            Span<byte> blob = GetBlob("macroRequest.bin");
            
            int bytesRead = Reader.ReadMacroRequest(blob, out CodeChannel channel, out bool reportMissing, out bool fromCode, out string filename);
            Assert.AreEqual(16, bytesRead);
            
            // Header
            Assert.AreEqual(DuetAPI.CodeChannel.USB, channel);
            Assert.IsFalse(reportMissing);
            Assert.IsTrue(fromCode);
            
            // Message
            Assert.AreEqual("homeall.g", filename);
        }

        [Test]
        public void AbortFile()
        {
            Span<byte> blob = GetBlob("abortFile.bin");

            int bytesRead = Reader.ReadAbortFile(blob, out CodeChannel channel, out bool abortAll);
            Assert.AreEqual(4, bytesRead);

            // Header
            Assert.AreEqual(DuetAPI.CodeChannel.File, channel);
            Assert.IsFalse(abortAll);
        }

        [Test]
        public void PrintPaused()
        {
            Span<byte> blob = GetBlob("printPaused.bin");
            
            int bytesRead = Reader.ReadPrintPaused(blob, out uint filePosition, out PrintPausedReason reason);
            Assert.AreEqual(8, bytesRead);
            
            // Header
            Assert.AreEqual(123456, filePosition);
            Assert.AreEqual(PrintPausedReason.GCode, reason);
        } 
        
        [Test]
        public void Heightmap()
        {
            Span<byte> blob = GetBlob("heightmap.bin");

            int bytesRead = Reader.ReadHeightMap(blob, out Heightmap map);
            Assert.AreEqual(80, bytesRead);
            
            // Header
            Assert.AreEqual(20, map.XMin, 0.0001);
            Assert.AreEqual(180, map.XMax, 0.0001);
            Assert.AreEqual(40, map.XSpacing, 0.0001);
            Assert.AreEqual(50, map.YMin, 0.0001);
            Assert.AreEqual(150, map.YMax, 0.0001);
            Assert.AreEqual(50, map.YSpacing, 0.0001);
            Assert.AreEqual(0, map.Radius, 0.0001);
            Assert.AreEqual(3, map.NumX);
            Assert.AreEqual(4, map.NumY);
            
            // Points
            Assert.AreEqual(12, map.ZCoordinates.Length);
            for (int i = 0; i < map.ZCoordinates.Length; i++)
            {
                Assert.AreEqual(map.ZCoordinates[i], 10 * i + 10, 0.0001);
            }
        }

        [Test]
        public void CodeChannel()
        {
            Span<byte> blob = GetBlob("codeChannel.bin");

            int bytesRead = Reader.ReadCodeChannel(blob, out CodeChannel channel);
            Assert.AreEqual(bytesRead, 4);

            // Header
            Assert.AreEqual(DuetAPI.CodeChannel.SBC, channel);
        }

        [Test]
        public void FileChunk()
        {
            Span<byte> blob = GetBlob("fileChunk.bin");

            int bytesRead = Reader.ReadFileChunkRequest(blob, out string filename, out uint offset, out uint maxLength);
            Assert.AreEqual(20, bytesRead);

            // Header
            Assert.AreEqual(1234, offset);
            Assert.AreEqual(5678, maxLength);

            // Filename
            Assert.AreEqual("test.bin", filename);
        }

        [Test]
        public void EvaluationResult()
        {
            Span<byte> blob = GetBlob("evaluationResult.bin");

            int bytesRead = Reader.ReadEvaluationResult(blob, out string expression, out object result);
            Assert.AreEqual(32, bytesRead);

            // Header
            Assert.AreEqual(300, (int)result);

            // Expression
            Assert.AreEqual("move.axes[0].position", expression);
        }

        [Test]
        public void DoCode()
        {
            Span<byte> blob = GetBlob("doCode.bin");

            int bytesRead = Reader.ReadDoCode(blob, out CodeChannel channel, out string code);
            Assert.AreEqual(24, bytesRead);

            // Header
            Assert.AreEqual(DuetAPI.CodeChannel.Aux, channel);

            // Code
            Assert.AreEqual("M20 S2 P\"0:/macros\"", code);
        }

        private Span<byte> GetBlob(string filename)
        {
            FileStream stream = new FileStream(Path.Combine(Directory.GetCurrentDirectory(), "../../../SPI/Blobs", filename), FileMode.Open, FileAccess.Read);
            Span<byte> content = new byte[stream.Length];
            stream.Read(content);
            stream.Close();
            return content;
        }
    }
}