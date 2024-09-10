using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.SPI.Serialization;
using NUnit.Framework;
using NUnit.Framework.Legacy;

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
            ClassicAssert.AreEqual(Consts.FormatCode, header.FormatCode);
            ClassicAssert.AreEqual(4, header.NumPackets);
            ClassicAssert.AreEqual(Consts.ProtocolVersion, header.ProtocolVersion);
            ClassicAssert.AreEqual(12345, header.SequenceNumber);
            ClassicAssert.AreEqual(1436, header.DataLength);
            ClassicAssert.AreEqual(0, header.ChecksumData32);
            ClassicAssert.AreEqual(0, header.ChecksumHeader32);
            
            // No padding
        }

        [Test]
        public void PacketHeader()
        {
            Span<byte> blob = GetBlob("packetHeader.bin");
            
            int bytesRead = Reader.ReadPacketHeader(blob, out PacketHeader header);
            ClassicAssert.AreEqual(8, bytesRead);
            
            // Header
            ClassicAssert.AreEqual((ushort)Request.ObjectModel, header.Request);
            ClassicAssert.AreEqual(12, header.Id);
            ClassicAssert.AreEqual(300, header.Length);
        }

        [Test]
        public void PacketHeaderResend()
        {
            Span<byte> blob = GetBlob("packetHeaderResend.bin");

            int bytesRead = Reader.ReadPacketHeader(blob, out PacketHeader header);
            ClassicAssert.AreEqual(8, bytesRead);

            // Header
            ClassicAssert.AreEqual((ushort)Request.ResendPacket, header.Request);
            ClassicAssert.AreEqual(23, header.Id);
            ClassicAssert.AreEqual(0, header.Length);
            ClassicAssert.AreEqual(12, header.ResendPacketId);
        }

        [Test]
        public void StringRequest()
        {
            Span<byte> blob = GetBlob("stringRequest.bin");
            
            int bytesRead = Reader.ReadStringRequest(blob, out ReadOnlySpan<byte> json);
            ClassicAssert.AreEqual(24, bytesRead);
            
            // JSON
            ClassicAssert.AreEqual("{\"hello\":\"json!\"}", Encoding.UTF8.GetString(json));
        }

        [Test]
        public void CodeBufferUpdate()
        {
            Span<byte> blob = GetBlob("codeBufferUpdate.bin");

            int bytesRead = Reader.ReadCodeBufferUpdate(blob, out ushort bufferSpace);
            ClassicAssert.AreEqual(4, bytesRead);

            // Header
            ClassicAssert.AreEqual(787, bufferSpace);
        }

        [Test]
        public void Message()
        {
            Span<byte> blob = GetBlob("message.bin");

            int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
            ClassicAssert.AreEqual(28, bytesRead);

            // Header
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.HttpMessage));
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.TelnetMessage));
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.UsbMessage));
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.AuxMessage));
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.WarningMessageFlag));
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.PushFlag));
            
            // Message
            ClassicAssert.AreEqual("This is just a test", reply);
        }
        
        [Test]
        public void EmptyMessage()
        {
            Span<byte> blob = GetBlob("emptyMessage.bin");

            int bytesRead = Reader.ReadMessage(blob, out MessageTypeFlags messageType, out string reply);
            ClassicAssert.AreEqual(8, bytesRead);

            // Header
            ClassicAssert.IsTrue(messageType.HasFlag(MessageTypeFlags.UsbMessage));

            // Message
            ClassicAssert.IsEmpty(reply);
        }
        
        [Test]
        public void MacroRequest()
        {
            Span<byte> blob = GetBlob("macroRequest.bin");
            
            int bytesRead = Reader.ReadMacroRequest(blob, out CodeChannel channel, out bool fromCode, out string filename);
            ClassicAssert.AreEqual(16, bytesRead);
            
            // Header
            ClassicAssert.AreEqual(DuetAPI.CodeChannel.USB, channel);
            ClassicAssert.IsTrue(fromCode);
            
            // Message
            ClassicAssert.AreEqual("homeall.g", filename);
        }

        [Test]
        public void AbortFile()
        {
            Span<byte> blob = GetBlob("abortFile.bin");

            int bytesRead = Reader.ReadAbortFile(blob, out CodeChannel channel, out bool abortAll);
            ClassicAssert.AreEqual(4, bytesRead);

            // Header
            ClassicAssert.AreEqual(DuetAPI.CodeChannel.File, channel);
            ClassicAssert.IsFalse(abortAll);
        }

        [Test]
        public void PrintPaused()
        {
            Span<byte> blob = GetBlob("printPaused.bin");
            
            int bytesRead = Reader.ReadPrintPaused(blob, out uint filePosition, out PrintPausedReason reason);
            ClassicAssert.AreEqual(8, bytesRead);
            
            // Header
            ClassicAssert.AreEqual(123456, filePosition);
            ClassicAssert.AreEqual(PrintPausedReason.GCode, reason);
        } 

        [Test]
        public void CodeChannel()
        {
            Span<byte> blob = GetBlob("codeChannel.bin");

            int bytesRead = Reader.ReadCodeChannel(blob, out CodeChannel channel);
            ClassicAssert.AreEqual(bytesRead, 4);

            // Header
            ClassicAssert.AreEqual(DuetAPI.CodeChannel.SBC, channel);
        }

        [Test]
        public void FileChunk()
        {
            Span<byte> blob = GetBlob("fileChunk.bin");

            int bytesRead = Reader.ReadFileChunkRequest(blob, out string filename, out uint offset, out int maxLength);
            ClassicAssert.AreEqual(20, bytesRead);

            // Header
            ClassicAssert.AreEqual(1234, offset);
            ClassicAssert.AreEqual(5678, maxLength);

            // Filename
            ClassicAssert.AreEqual("test.bin", filename);
        }

        [Test]
        public void EvaluationResult()
        {
            Span<byte> blob = GetBlob("evaluationResult.bin");

            int bytesRead = Reader.ReadEvaluationResult(blob, out string expression, out object result);
            ClassicAssert.AreEqual(32, bytesRead);

            // Header
            ClassicAssert.AreEqual(300, (int)result!);

            // Expression
            ClassicAssert.AreEqual("move.axes[0].position", expression);
        }

        [Test]
        public void DoCode()
        {
            Span<byte> blob = GetBlob("doCode.bin");

            int bytesRead = Reader.ReadDoCode(blob, out CodeChannel channel, out string code);
            ClassicAssert.AreEqual(24, bytesRead);

            // Header
            ClassicAssert.AreEqual(DuetAPI.CodeChannel.Aux, channel);

            // Code
            ClassicAssert.AreEqual("M20 S2 P\"0:/macros\"", code);
        }

        private static Span<byte> GetBlob(string filename)
        {
            FileStream stream = new(Path.Combine(Directory.GetCurrentDirectory(), "../../../SPI/Blobs", filename), FileMode.Open, FileAccess.Read);
            Span<byte> content = new byte[stream.Length];
            stream.Read(content);
            stream.Close();
            return content;
        }
    }
}