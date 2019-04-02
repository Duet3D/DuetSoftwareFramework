using System;
using System.IO;
using DuetAPI;
using DuetAPI.Commands;
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
            
            TransferHeader header = Reader.ReadTransferHeader(blob);
            
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
        public void ReadState()
        {
            Span<byte> blob = GetBlob("state.bin");

            int bytesRead = Reader.ReadState(blob, out CodeChannel[] busyChannels);
            Assert.AreEqual(bytesRead, 4);

            // Header
            Assert.AreEqual(2, busyChannels.Length);
            Assert.AreEqual(CodeChannel.USB, busyChannels[0]);
            Assert.AreEqual(CodeChannel.AUX, busyChannels[1]);
        }

        [Test]
        public void ReadObjectModel()
        {
            Span<byte> blob = GetBlob("objectModel.bin");
            
            int bytesRead = Reader.ReadObjectModel(blob, 21, out byte module, out string json);
            Assert.AreEqual(24, bytesRead);
            
            // Header
            Assert.AreEqual(4, module);
            
            // JSON
            Assert.AreEqual("{\"hello\":\"json\"}", json);
        }

        [Test]
        public void ReadCodeReply()
        {
            Span<byte> blob = GetBlob("codeReply.bin");
            
            int bytesRead = Reader.ReadCodeReply(blob, 25, out CodeChannel[] channels, out Message message, out bool pushFlag);
            Assert.AreEqual(28, bytesRead);
            
            // Header
            Assert.AreEqual(4, channels.Length);
            Assert.AreEqual(CodeChannel.HTTP, channels[0]);
            Assert.AreEqual(CodeChannel.Telnet, channels[1]);
            Assert.AreEqual(CodeChannel.USB, channels[2]);
            Assert.AreEqual(CodeChannel.AUX, channels[3]);
            Assert.AreEqual(true, pushFlag);
            
            // Message
            Assert.AreEqual(MessageType.Warning, message.Type);
            Assert.AreEqual("This is just a test!", message.Content);
        }
        
        [Test]
        public void ReadEmptyCodeReply()
        {
            Span<byte> blob = GetBlob("emptyCodeReply.bin");
            
            int bytesRead = Reader.ReadCodeReply(blob, 4, out CodeChannel[] channels, out Message message, out bool pushFlag);
            Assert.AreEqual(4, bytesRead);

            // Header
            Assert.AreEqual(1, channels.Length);
            Assert.AreEqual(CodeChannel.USB, channels[0]);
            Assert.AreEqual(false, pushFlag);
            
            // Message
            Assert.AreEqual(MessageType.Success, message.Type);
            Assert.AreEqual("", message.Content);
        }
        
        [Test]
        public void ReadMacroRequest()
        {
            Span<byte> blob = GetBlob("macroRequest.bin");
            
            int bytesRead = Reader.ReadMacroRequest(blob, 14, out CodeChannel channel, out bool reportMissing, out string filename);
            Assert.AreEqual(16, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.USB, channel);
            Assert.AreEqual(false, reportMissing);
            
            // Message
            Assert.AreEqual("homeall.g", filename);
        }

        [Test]
        public void ReadAbortFile()
        {
            Span<byte> blob = GetBlob("abortFile.bin");

            int bytesRead = Reader.ReadAbortFile(blob, out CodeChannel channel);
            Assert.AreEqual(4, bytesRead);

            // Header
            Assert.AreEqual(CodeChannel.File, channel);
        }

        [Test]
        public void ReadStackEvent()
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
        public void ReadPrintPaused()
        {
            Span<byte> blob = GetBlob("printPaused.bin");
            
            int bytesRead = Reader.ReadPrintPaused(blob, out uint filePosition, out PrintPausedReason reason);
            Assert.AreEqual(8, bytesRead);
            
            // Header
            Assert.AreEqual(123456, filePosition);
            Assert.AreEqual(PrintPausedReason.GCode, reason);
        } 
        
        [Test]
        public void ReadHeightmap()
        {
            Span<byte> blob = GetBlob("heightmap.bin");
            
            int bytesRead = Reader.ReadHeightMap(blob, out HeightMap header, out Span<float> zCoordinates);
            Assert.AreEqual(80, bytesRead);
            
            // Header
            Assert.AreEqual(20, header.XMin, 0.0001);
            Assert.AreEqual(180, header.XMax, 0.0001);
            Assert.AreEqual(40, header.XSpacing, 0.0001);
            Assert.AreEqual(50, header.YMin, 0.0001);
            Assert.AreEqual(150, header.YMax, 0.0001);
            Assert.AreEqual(50, header.YSpacing, 0.0001);
            Assert.AreEqual(12, header.NumPoints, 0.0001);
            Assert.AreEqual(50, header.YSpacing, 0.0001);
            Assert.AreEqual(0, header.Radius, 0.0001);
            Assert.AreEqual(12, header.NumPoints);
            
            // Points
            Assert.AreEqual(12, zCoordinates.Length);
            for (int i = 0; i < zCoordinates.Length; i++)
            {
                Assert.AreEqual(zCoordinates[i], 10 * i + 10, 0.0001);
            }
        }

        [Test]
        public void ReadResourceLocked()
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