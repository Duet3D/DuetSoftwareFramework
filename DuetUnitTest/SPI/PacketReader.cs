using System;
using System.IO;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.DuetRequests;
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
            Assert.AreEqual(300, header.Length);
            
            // No padding
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
            
            int bytesRead = Reader.ReadCodeReply(blob, 25, out CodeChannel channel, out Message message, out bool isCodeComplete);
            Assert.AreEqual(28, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.HTTP, channel);
            Assert.AreEqual(true, isCodeComplete);
            
            // Message
            Assert.AreEqual(MessageType.Warning, message.Type);
            Assert.AreEqual("This is just a test!", message.Content);
        }
        
        [Test]
        public void ReadEmptyCodeReply()
        {
            Span<byte> blob = GetBlob("emptyCodeReply.bin");
            
            int bytesRead = Reader.ReadCodeReply(blob, 4, out CodeChannel channel, out Message message, out bool isCodeComplete);
            Assert.AreEqual(4, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.SPI, channel);
            Assert.AreEqual(true, isCodeComplete);
            
            // Message
            Assert.AreEqual(MessageType.Success, message.Type);
            Assert.AreEqual("", message.Content);
        }
        
        [Test]
        public void ReadMacroRequest()
        {
            Span<byte> blob = GetBlob("macroRequest.bin");
            
            int bytesRead = Reader.ReadMacroRequest(blob, 14, out CodeChannel channel, out string filename);
            Assert.AreEqual(16, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.SPI, channel);
            
            // Message
            Assert.AreEqual("homeall.g", filename);
        }
               
        [Test]
        public void ReadStackEvent()
        {
            Span<byte> blob = GetBlob("stackEvent.bin");
            
            int bytesRead = Reader.ReadStackEvent(blob, out CodeChannel channel, out byte stackDepth);
            Assert.AreEqual(4, bytesRead);
            
            // Header
            Assert.AreEqual(CodeChannel.Telnet, channel);
            Assert.AreEqual(5, stackDepth);
        } 
                      
        [Test]
        public void ReadPrintPaused()
        {
            Span<byte> blob = GetBlob("printPaused.bin");
            
            int bytesRead = Reader.ReadPrintPaused(blob, out uint filePosition);
            Assert.AreEqual(4, bytesRead);
            
            // Header
            Assert.AreEqual(123456, filePosition);
        } 
        
        [Test]
        public void ReadHeightmap()
        {
            Span<byte> blob = GetBlob("heightmap.bin");
            
            int bytesRead = Reader.ReadHeightmap(blob, 72, out HeightmapHeader header, out Span<float> zCoordinates);
            Assert.AreEqual(72, bytesRead);
            
            // Header
            Assert.AreEqual(20, header.XStart, 0.0001);
            Assert.AreEqual(180, header.XEnd, 0.0001);
            Assert.AreEqual(40, header.XSpacing, 0.0001);
            Assert.AreEqual(50, header.YStart, 0.0001);
            Assert.AreEqual(150, header.YEnd, 0.0001);
            Assert.AreEqual(50, header.YSpacing, 0.0001);
            
            // Points
            Assert.AreEqual(12, zCoordinates.Length);
            for (int i = 0; i < zCoordinates.Length; i++)
            {
                Assert.AreEqual(zCoordinates[i], 10 * i + 10, 0.0001);
            }
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