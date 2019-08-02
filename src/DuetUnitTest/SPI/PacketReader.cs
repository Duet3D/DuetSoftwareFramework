using System;
using System.IO;
using System.Runtime.InteropServices;
using DuetAPI;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Serialization;
using NUnit.Framework;

namespace DuetUnitTest.SPI
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
        public void ObjectModel()
        {
            Span<byte> blob = GetBlob("objectModel.bin");
            
            int bytesRead = Reader.ReadObjectModel(blob, out byte module, out string json);
            Assert.AreEqual(24, bytesRead);
            
            // Header
            Assert.AreEqual(4, module);
            
            // JSON
            Assert.AreEqual("{\"hello\":\"json!\"}", json);
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
        public void CodeReply()
        {
            Span<byte> blob = GetBlob("codeReply.bin");

            int bytesRead = Reader.ReadCodeReply(blob, out MessageTypeFlags messageType, out string reply);
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
        public void EmptyCodeReply()
        {
            Span<byte> blob = GetBlob("emptyCodeReply.bin");

            int bytesRead = Reader.ReadCodeReply(blob, out MessageTypeFlags messageType, out string reply);
            Assert.AreEqual(8, bytesRead);

            // Header
            Assert.IsTrue(messageType.HasFlag(MessageTypeFlags.UsbMessage));

            // Message
            Assert.AreEqual("", reply);
        }
        
        [Test]
        public void MacroRequest()
        {
            Span<byte> blob = GetBlob("macroRequest.bin");
            
            int bytesRead = Reader.ReadMacroRequest(blob, out CodeChannel channel, out bool reportMissing, out bool fromCode, out string filename);
            Assert.AreEqual(16, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.USB, channel);
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
            Assert.AreEqual(CodeChannel.File, channel);
            Assert.IsFalse(abortAll);
        }

        [Test]
        public void StackEvent()
        {
            Span<byte> blob = GetBlob("stackEvent.bin");
            
            int bytesRead = Reader.ReadStackEvent(blob, out CodeChannel channel, out byte stackDepth, out StackFlags flags, out float feedrate);
            Assert.AreEqual(8, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.File, channel);
            Assert.AreEqual(5, stackDepth);
            Assert.AreEqual(StackFlags.DrivesRelative, flags);
            Assert.AreEqual(3000, feedrate, 0.0001);
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
        public void ResourceLocked()
        {
            Span<byte> blob = GetBlob("resourceLocked.bin");

            int bytesRead = Reader.ReadResourceLocked(blob, out CodeChannel channel);
            Assert.AreEqual(bytesRead, 4);

            // Header
            Assert.AreEqual(CodeChannel.SPI, channel);
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