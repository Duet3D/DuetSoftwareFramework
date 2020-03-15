using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
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
    public static class Reader
    {
        /// <summary>
        /// Read a packet header from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <returns>Header describing a packet</returns>
        public static PacketHeader ReadPacketHeader(ReadOnlySpan<byte> from)
        {
            return MemoryMarshal.Cast<byte, PacketHeader>(from)[0];
        }

        /// <summary>
        /// Read an object model header plus JSON text from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="module">Number of the module from which the JSON data originates</param>
        /// <param name="json">Object model data as JSON or null if none available</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadObjectModel(ReadOnlySpan<byte> from, out byte module, out byte[] json)
        {
            ObjectModel header = MemoryMarshal.Cast<byte, ObjectModel>(from)[0];
            int bytesRead = Marshal.SizeOf(header);
            
            module = header.Module;
            if (header.Length > 0)
            {
                json = from.Slice(bytesRead, header.Length).ToArray();
                bytesRead += header.Length;
            }
            else
            {
                json = null;
            }
            
            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a code buffer update from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="bufferSpace">Buffer space</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeBufferUpdate(ReadOnlySpan<byte> from, out ushort bufferSpace)
        {
            CodeBufferUpdate header = MemoryMarshal.Cast<byte, CodeBufferUpdate>(from)[0];

            // Read header
            bufferSpace = header.BufferSpace;

            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a code reply from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="messageType">Message flags</param>
        /// <param name="reply">Raw code reply</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeReply(ReadOnlySpan<byte> from, out MessageTypeFlags messageType, out string reply)
        {
            CodeReply header = MemoryMarshal.Cast<byte, CodeReply>(from)[0];
            int bytesRead = Marshal.SizeOf(header);

            // Read header
            messageType = (MessageTypeFlags)header.MessageType;

            // Read message content
            if (header.Length > 0)
            {
                ReadOnlySpan<byte> unicodeReply = from.Slice(bytesRead, header.Length);
                reply = Encoding.UTF8.GetString(unicodeReply);
                bytesRead += header.Length;
            }
            else
            {
                reply = string.Empty;
            }

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a macro file request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel that requested the execution</param>
        /// <param name="reportMissing">Output a message if the macro cannot be found</param>
        /// <param name="fromCode">Whether the macro request came from the G/M/T-code being executed</param>
        /// <param name="filename">Filename of the macro to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMacroRequest(ReadOnlySpan<byte> from, out CodeChannel channel, out bool reportMissing, out bool fromCode, out string filename)
        {
            MacroRequest header = MemoryMarshal.Cast<byte, MacroRequest>(from)[0];
            int bytesRead = Marshal.SizeOf(header);
 
            // Read header
            channel = header.Channel;
            reportMissing = Convert.ToBoolean(header.ReportMissing);
            fromCode = Convert.ToBoolean(header.FromCode);

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, header.Length);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read information about an abort file request 
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel running the file</param>
        /// <param name="abortAll">Whether all files are supposed to be aborted</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadAbortFile(ReadOnlySpan<byte> from, out CodeChannel channel, out bool abortAll)
        {
            AbortFileRequest header = MemoryMarshal.Cast<byte, AbortFileRequest>(from)[0];
            channel = (CodeChannel)header.Channel;
            abortAll = header.AbortAll != 0;
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
        public static int ReadStackEvent(ReadOnlySpan<byte> from, out CodeChannel channel, out byte stackDepth, out StackFlags flags, out float feedrate)
        {
            StackEvent header = MemoryMarshal.Cast<byte, StackEvent>(from)[0];
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
        public static int ReadPrintPaused(ReadOnlySpan<byte> from, out uint filePosition, out PrintPausedReason reason)
        {
            PrintPaused header = MemoryMarshal.Cast<byte, PrintPaused>(from)[0];
            filePosition = header.FilePosition;
            reason = (PrintPausedReason)header.PauseReason;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a heightmap report
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="map">Deserialized heightmap</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadHeightMap(ReadOnlySpan<byte> from, out DuetAPI.Utility.Heightmap map)
        {
            HeightMap header = MemoryMarshal.Cast<byte, HeightMap>(from)[0];
            map = new DuetAPI.Utility.Heightmap
            {
                XMin = header.XMin,
                XMax = header.XMax,
                XSpacing = header.XSpacing,
                YMin = header.YMin,
                YMax = header.YMax,
                YSpacing = header.YSpacing,
                Radius = header.Radius,
                NumX = header.NumX,
                NumY = header.NumY
            };

            if (from.Length > Marshal.SizeOf(header))
            {
                ReadOnlySpan<byte> zCoordinates = from.Slice(Marshal.SizeOf(header), Marshal.SizeOf(typeof(float)) * map.NumX * map.NumY);
                map.ZCoordinates = MemoryMarshal.Cast<byte, float>(zCoordinates).ToArray();
            }
            else
            {
                map.NumX = map.NumY = 0;
                map.ZCoordinates = Array.Empty<float>();
            }
            return Marshal.SizeOf(header) + map.ZCoordinates.Length * Marshal.SizeOf(typeof(float));
        }

        /// <summary>
        /// Read a lock confirmation
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Channel that has acquired the lock</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadResourceLocked(ReadOnlySpan<byte> from, out CodeChannel channel)
        {
            LockUnlock header = MemoryMarshal.Cast<byte, LockUnlock>(from)[0];
            channel = header.Channel;
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Read a file chunk request`
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filename">Filename to read from</param>
        /// <param name="offset">Offset in the file</param>
        /// <param name="maxLength">Maximum chunk length</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadFileChunkRequest(ReadOnlySpan<byte> from, out string filename, out uint offset, out uint maxLength)
        {
            FileChunkRequest header = MemoryMarshal.Cast<byte, FileChunkRequest>(from)[0];
            int bytesRead = Marshal.SizeOf(header);

            // Read header
            offset = header.Offset;
            maxLength = header.MaxLength;

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, (int)header.FilenameLength);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += (int)header.FilenameLength;

            return AddPadding(bytesRead);
        }

        private static int AddPadding(int bytesRead)
        {
            int padding = 4 - bytesRead % 4;
            return (padding == 4) ? bytesRead : bytesRead + padding;
        }
    }
}