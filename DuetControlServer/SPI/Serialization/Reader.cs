using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.DuetRequests;

namespace DuetControlServer.SPI.Serialization
{
    /// <summary>
    /// Static class for reading data from SPI transmissions.
    /// It is expected that each data block occupies entire 4-byte blocks.
    /// Make sure to keep the data returned by these functions only as long as the underlying buffer is actually valid!
    /// </summary>
    /// <remarks>Once C# 3.0 is out, the performance of this can be further improved (without relying on unsafe code) by using <c>Span.AsRef()</c></remarks>
    public static class Reader
    {
        /// <summary>
        /// Read a transfer header from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <returns>Header describing a data transfer</returns>
        public static TransferHeader ReadTransferHeader(Span<byte> from)
        {
            return MemoryMarshal.Read<TransferHeader>(from);
        }

        /// <summary>
        /// Read a packet header from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <returns>Header describing a packet</returns>
        public static PacketHeader ReadPacketHeader(Span<byte> from)
        {
            return MemoryMarshal.Read<PacketHeader>(from);
        }

        /// <summary>
        /// Read an object model header plus JSON text from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packetLength">Length of the received packet</param>
        /// <param name="module">Number of the module from which the JSON data originates</param>
        /// <param name="json">Object model data as JSON</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadObjectModel(Span<byte> from, int packetLength, out byte module, out string json)
        {
            ObjectModel header = MemoryMarshal.Read<ObjectModel>(from);
            int bytesRead = Marshal.SizeOf(header);
            
            module = header.Module;
            if (packetLength > bytesRead)
            {
                Span<byte> unicodeJSON = from.Slice(bytesRead, packetLength - bytesRead - 1);
                json = Encoding.UTF8.GetString(unicodeJSON);
            }
            else
            {
                json = null;
            }
            
            return AddPadding(packetLength);
        }

        /// <summary>
        /// Read a code reply from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packetLength">Length of the received packet</param>
        /// <param name="channel">Channel from which this message originates</param>
        /// <param name="message">Generated message</param>
        /// <param name="isCodeComplete">Indicates if this the last code has finished its execution</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeReply(Span<byte> from, int packetLength, out CodeChannel channel, out Message message, out bool isCodeComplete)
        {
            CodeReply header = MemoryMarshal.Read<CodeReply>(from);
            int bytesRead = Marshal.SizeOf(header);
            
            // Read the message content
            string content;
            if (packetLength > bytesRead)
            {
                Span<byte> unicodeReply = from.Slice(bytesRead, packetLength - bytesRead - 1);
                content = Encoding.UTF8.GetString(unicodeReply);
            }
            else
            {
                content = "";
            }
            
            // Generate the message
            channel = header.Channel;
            isCodeComplete = header.Flags.HasFlag(CodeReplyFlags.CodeComplete);
            message = new Message
            {
                Content = content,
                Time = DateTime.Now,
                Type = header.Flags.HasFlag(CodeReplyFlags.Error) ? MessageType.Error : (header.Flags.HasFlag(CodeReplyFlags.Warning) ? MessageType.Warning : MessageType.Success)
            };
            
            return AddPadding(packetLength);
        }

        /// <summary>
        /// Read a macro file request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packetLength">Length of the received packet</param>
        /// <param name="channel">Code channel that requested the execution</param>
        /// <param name="filename">Filename of the macro to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMacroRequest(Span<byte> from, int packetLength, out CodeChannel channel, out string filename)
        {
            MacroRequest header = MemoryMarshal.Read<MacroRequest>(from);
            int bytesRead = Marshal.SizeOf(header);
            
            channel = header.Channel;
            Span<byte> unicodeFilename = from.Slice(bytesRead, packetLength - bytesRead - 1);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            
            return AddPadding(packetLength);
        }
        
        /// <summary>
        /// Read a stack event (<see cref="Request.StackPushed"/> and <see cref="Request.StackPopped"/>)
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel where the stack event occurred</param>
        /// <param name="stackDepth">New stack depth</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadStackEvent(Span<byte> from, out CodeChannel channel, out byte stackDepth)
        {
            StackEvent header = MemoryMarshal.Read<StackEvent>(from);
            channel = header.Channel;
            stackDepth = header.StackDepth;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a print pause event
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filePosition">Position at which the print has been paused</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPrintPaused(Span<byte> from, out uint filePosition)
        {
            PrintPaused header = MemoryMarshal.Read<PrintPaused>(from);
            filePosition = header.FilePosition;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a heightmap report
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packetLength">Position at which the print has been paused</param>
        /// <param name="header"></param>
        /// <param name="zCoordinates"></param>
        /// <returns>Number of bytes read</returns>
        public static int ReadHeightmap(Span<byte> from, int packetLength, out HeightmapHeader header, out Span<float> zCoordinates)
        {
            header = MemoryMarshal.Read<HeightmapHeader>(from);
            zCoordinates = MemoryMarshal.Cast<byte, float>(from.Slice(Marshal.SizeOf(header)));
            return packetLength;
        }

        private static int AddPadding(int bytesRead)
        {
            int padding = 4 - bytesRead % 4;
            return (padding == 4) ? bytesRead : bytesRead + padding;
        }
    }
}