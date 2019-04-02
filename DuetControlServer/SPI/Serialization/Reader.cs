using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.SharedRequests;

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
        /// Read the current state of the code buffers
        /// </summary>
        /// <param name="from"></param>
        /// <param name="busyChannels"></param>
        /// <returns></returns>
        public static int ReadState(Span<byte> from, out CodeChannel[] busyChannels)
        {
            StateResponse header = MemoryMarshal.Read<StateResponse>(from);

            List<CodeChannel> busyChannelsList = new List<CodeChannel>();
            for (int i = 0; i < Consts.NumCodeChannels; i++)
            {
                if ((header.BusyChannels & (1 << i)) != 0)
                {
                    busyChannelsList.Add((CodeChannel)i);
                }
            }
            busyChannels = busyChannelsList.ToArray();

            return Marshal.SizeOf(header);
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
        /// <param name="channels">Channel destinations</param>
        /// <param name="message">Generated message</param>
        /// <param name="pushFlag">True if the reply has been truncated</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeReply(Span<byte> from, int packetLength, out CodeChannel[] channels, out Message message, out bool pushFlag)
        {
            CodeReply header = MemoryMarshal.Read<CodeReply>(from);
            int bytesRead = Marshal.SizeOf(header);
            
            // Read message destinations
            List<CodeChannel> channelsList = new List<CodeChannel>();
            for (int i = 0; i < Consts.NumCodeChannels; i++)
            {
                if ((header.MessageType & (1 << i)) != 0)
                {
                    channelsList.Add((CodeChannel)i);
                }
            }
            channels = channelsList.ToArray();

            // Read message content
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

            // Read message flags
            MessageTypeFlags type = (MessageTypeFlags)header.MessageType;
            message = new Message
            {
                Content = content,
                Time = DateTime.Now,
                Type = type.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                        : (type.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                        : MessageType.Success)
            };
            pushFlag = type.HasFlag(MessageTypeFlags.PushFlag);
            return AddPadding(packetLength);
        }

        /// <summary>
        /// Read a macro file request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packetLength">Length of the received packet</param>
        /// <param name="channel">Code channel that requested the execution</param>
        /// <param name="reportMissing">Output a message if the macro cannot be found</param>
        /// <param name="filename">Filename of the macro to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMacroRequest(Span<byte> from, int packetLength, out CodeChannel channel, out bool reportMissing, out string filename)
        {
            MacroRequest header = MemoryMarshal.Read<MacroRequest>(from);
            int bytesRead = Marshal.SizeOf(header);
            
            channel = header.Channel;
            reportMissing = Convert.ToBoolean(header.ReportMissing);
            Span<byte> unicodeFilename = from.Slice(bytesRead, packetLength - bytesRead - 1);
            filename = Encoding.UTF8.GetString(unicodeFilename);

            return AddPadding(packetLength);
        }

        /// <summary>
        /// Read information about an abort file request 
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel running the file</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadAbortFile(Span<byte> from, out CodeChannel channel)
        {
            AbortFileRequest header = MemoryMarshal.Read<AbortFileRequest>(from);
            channel = (CodeChannel)header.Channel;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a stack event
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel where the stack event occurred</param>
        /// <param name="stackDepth">New stack depth</param>
        /// <param name="flags">Flags of the stack</param>
        /// <param name="feedrate">Feedrate in mm/s</param>
        /// <returns>Number of bytes read</returns>
        /// <seealso cref="Request.StackEvent"/>
        public static int ReadStackEvent(Span<byte> from, out CodeChannel channel, out byte stackDepth, out StackFlags flags, out float feedrate)
        {
            StackEvent header = MemoryMarshal.Read<StackEvent>(from);
            channel = (CodeChannel)header.Channel;
            stackDepth = header.StackDepth;
            flags = (StackFlags)header.Flags;
            feedrate = header.Feedrate;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a print pause event
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filePosition">Position at which the print has been paused</param>
        /// <param name="reason">Reason why the print has been paused</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPrintPaused(Span<byte> from, out uint filePosition, out PrintPausedReason reason)
        {
            PrintPaused header = MemoryMarshal.Read<PrintPaused>(from);
            filePosition = header.FilePosition;
            reason = (PrintPausedReason)header.PauseReason;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a heightmap report
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="header"></param>
        /// <param name="zCoordinates"></param>
        /// <returns>Number of bytes read</returns>
        public static int ReadHeightMap(Span<byte> from, out HeightMap header, out Span<float> zCoordinates)
        {
            header = MemoryMarshal.Read<HeightMap>(from);
            zCoordinates = MemoryMarshal.Cast<byte, float>(from.Slice(Marshal.SizeOf(header), (int)header.NumPoints * Marshal.SizeOf(typeof(float))));
            return Marshal.SizeOf(header) + zCoordinates.Length * Marshal.SizeOf(typeof(float));
        }

        /// <summary>
        /// Read a lock confirmation
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Channel that has acquired the lock</param>
        /// <returns></returns>
        public static int ReadResourceLocked(Span<byte> from, out CodeChannel channel)
        {
            LockUnlock header = MemoryMarshal.Read<LockUnlock>(from);
            channel = (CodeChannel)header.Channel;
            return Marshal.SizeOf(header);
        }

        private static int AddPadding(int bytesRead)
        {
            int padding = 4 - bytesRead % 4;
            return (padding == 4) ? bytesRead : bytesRead + padding;
        }
    }
}