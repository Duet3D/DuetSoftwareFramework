using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.LinuxRequests;
using Code = DuetControlServer.Commands.Code;
using CodeParameter = DuetControlServer.SPI.Communication.LinuxRequests.CodeParameter;

namespace DuetControlServer.SPI.Serialization
{
    /// <summary>
    /// Static class for writing data for SPI transmissions.
    /// This class makes sure each data block is on a 4-byte boundary to guarantee efficient DMA transfers on the remote side.
    /// </summary>
    public static class Writer
    {
        /// <summary>
        /// Size of a transmission header
        /// </summary>
        public static readonly int TransmissionHeaderSize = Marshal.SizeOf(typeof(TransferHeader));
        
        /// <summary>
        /// Write a transfer header to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="numPackets">Number of packets to send</param>
        /// <param name="sequenceNumber">Sequence number</param>
        /// <param name="transferLength">Total length of the transfer in bytes</param>
        /// <param name="checksum">Checksum (unused)</param>
        public static void WriteTransferHeader(Span<byte> to, byte numPackets, uint sequenceNumber, ushort transferLength, ushort checksum)
        {
            TransferHeader header = new TransferHeader
            {
                Checksum = checksum,
                FormatCode =  Consts.FormatCode,
                Length = transferLength,
                NumPackets = numPackets,
                ProtocolVersion = Consts.ProtocolVersion,
                SequenceNumber = sequenceNumber
            };
            MemoryMarshal.Write(to, ref header);
        }
        
        /// <summary>
        /// Size of a packet header
        /// </summary>
        public static readonly int PacketHeaderSize = Marshal.SizeOf(typeof(PacketHeader));
        
        /// <summary>
        /// Write an arbitrary packet header to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="request">Packet type</param>
        /// <param name="packetId">ID of the packet</param>
        /// <param name="length">Length of the packet</param>
        public static void WritePacketHeader(Span<byte> to, Request request, ushort packetId, int length)
        {
            PacketHeader header = new PacketHeader()
            {
                Checksum = 0,        // TBD. In a future version this will be automatically determined
                Length = (ushort)length,
                PacketId = packetId,
                Request = (ushort)request
            };
            MemoryMarshal.Write(to, ref header);
        }
        
        /// <summary>
        /// Write a parsed G/M/T code in binary format to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="code">Code to write</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteCode(Span<byte> to, Code code)
        {
            int bytesWritten = 0;
            
            // Write code header
            CodeHeader header = new CodeHeader
            {
                Channel = code.Source,
                FilePosition = code.FilePosition,
                Letter = (byte)code.Type,
                MajorCode = code.MajorNumber,
                MinorCode = code.MinorNumber,
                NumParameters = (byte)code.Parameters.Count
            };
            if (code.IsPausable)
            {
                header.Flags |= CodeFlags.Pausable;
            }
            if (code.EnforceAbsoluteCoordinates)
            {
                header.Flags |= CodeFlags.EnforceAbsolutePosition;
            }
            if (code.IsFromConfig)
            {
                header.Flags |= CodeFlags.FromConfig;
            }
            if (code.IsFromConfigOverride)
            {
                header.Flags |= CodeFlags.FromConfigOverride;
            }
            
            MemoryMarshal.Write(to, ref header);
            bytesWritten += Marshal.SizeOf(header);
            
            // Write parameters
            List<object> extraParameters = new List<object>();
            foreach (var parameter in code.Parameters)
            {
                CodeParameter binaryParam = new CodeParameter();
                binaryParam.Letter = (byte)parameter.Letter;
                if (parameter.Type == typeof(int))
                {
                    binaryParam.Type = DataType.Int;
                    binaryParam.IntValue = parameter.AsInt;
                }
                else if (parameter.Type == typeof(uint))
                {
                    binaryParam.Type = DataType.UInt;
                    binaryParam.UIntValue = parameter.AsUInt;
                }
                else if (parameter.Type == typeof(float))
                {
                    binaryParam.Type = DataType.Float;
                    binaryParam.FloatValue = parameter.AsFloat;
                }
                else if (parameter.Type == typeof(int[]))
                {
                    binaryParam.Type = DataType.IntArray;
                    int[] array = parameter.AsIntArray;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(uint[]))
                {
                    binaryParam.Type = DataType.UIntArray;
                    uint[] array = parameter.AsUIntArray;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(float[]))
                {
                    binaryParam.Type = DataType.FloatArray;
                    float[] array = parameter.AsFloatArray;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(string))
                {
                    string value = parameter.AsString;
                    binaryParam.Type = (value.Contains('[') && value.Contains(']')) ? DataType.Expression : DataType.String;
                    binaryParam.IntValue = value.Length;
                    extraParameters.Add(value);
                }
                else
                {
                    throw new ArgumentException("Unsupported type", parameter.Type.Name);
                }
                
                MemoryMarshal.Write(to.Slice(bytesWritten), ref binaryParam);
                bytesWritten += Marshal.SizeOf(binaryParam);
            }
            
            // Write extra parameters
            foreach (object parameter in extraParameters)
            {
                if (parameter is int[] intArray)
                {
                    foreach (int val in intArray)
                    {
                        int value = val;
                        MemoryMarshal.Write(to.Slice(bytesWritten), ref value);
                        bytesWritten += Marshal.SizeOf(value);
                    }
                }
                else if (parameter is uint[] uintArray)
                {
                    foreach (uint val in uintArray)
                    {
                        uint value = val;
                        MemoryMarshal.Write(to.Slice(bytesWritten), ref value);
                        bytesWritten += Marshal.SizeOf(value);
                    }
                }
                else if (parameter is float[] floatArray)
                {
                    foreach (float val in floatArray)
                    {
                        float value = val;
                        MemoryMarshal.Write(to.Slice(bytesWritten), ref value);
                        bytesWritten += Marshal.SizeOf(value);
                    }
                }
                else if (parameter is string value)
                {
                    Span<byte> asUnicode = Encoding.UTF8.GetBytes(value);
                    asUnicode.CopyTo(to.Slice(bytesWritten));
                    bytesWritten += asUnicode.Length;
                    to[bytesWritten] = 0;
                    bytesWritten++;
                }
                else
                {
                    throw new ArgumentException("Unsupported type", parameter.GetType().Name);
                }
            }

            return AddPadding(to, bytesWritten);
        }
        
        /// <summary>
        /// Write a <see cref="GetObjectModel"/> request to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="module">Module to query the object model from</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteObjectModelRequest(Span<byte> to, byte module)
        {
            GetObjectModel request = new GetObjectModel
            {
                Module = module
            };
            MemoryMarshal.Write(to, ref request);
            return Marshal.SizeOf(request);
        }
        
        /// <summary>
        /// Request the update of an object model field to an arbitrary value via a <see cref="SetObjectModel"/> request
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="field">Path to the object model field</param>
        /// <param name="value">New value</param>
        /// <returns>Number of bytes written</returns>
        /// <remarks>value must be of type <see cref="DataType"/></remarks>
        public static int WriteObjectModel(Span<byte> to, string field, object value)
        {
            Span<byte> unicodeField = Encoding.UTF8.GetBytes(field);
            if (unicodeField.Length > 254)
            {
                throw new ArgumentException("Value is too long", nameof(field));
            }
            
            SetObjectModel request = new SetObjectModel
            {
                FieldLength = (byte)unicodeField.Length
            };
            int bytesWritten = Marshal.SizeOf(request) + unicodeField.Length + 1;
            
            // Write object value
            if (value is int intValue)
            {
                request.Type = DataType.Int;
                request.IntValue = intValue;
            }
            else if (value is uint uintValue)
            {
                request.Type = DataType.UInt;
                request.UIntValue = uintValue;
            }
            else if (value is float floatValue)
            {
                request.Type = DataType.Float;
                request.FloatValue = floatValue;
            }
            else if (value is int[] intArray)
            {
                request.Type = DataType.IntArray;
                request.IntValue = intArray.Length;
                foreach (int val in intArray)
                {
                    intValue = val;
                    MemoryMarshal.Write(to.Slice(bytesWritten), ref intValue);
                    bytesWritten += Marshal.SizeOf(intValue);
                }
            }
            else if (value is uint[] uintArray)
            {
                request.Type = DataType.UIntArray;
                request.IntValue = uintArray.Length;
                foreach (uint val in uintArray)
                {
                    uintValue = val;
                    MemoryMarshal.Write(to.Slice(bytesWritten), ref uintValue);
                    bytesWritten += Marshal.SizeOf(uintValue);
                }
            }
            else if (value is float[] floatArray)
            {
                request.Type = DataType.FloatArray;
                request.IntValue = floatArray.Length;
                foreach (float val in floatArray)
                {
                    floatValue = val;
                    MemoryMarshal.Write(to.Slice(bytesWritten), ref floatValue);
                    bytesWritten += Marshal.SizeOf(floatValue);
                }
            }
            else if (value is string stringValue)
            {
                Span<byte> asUnicode = Encoding.UTF8.GetBytes(stringValue);
                request.Type = DataType.String;
                request.IntValue = asUnicode.Length;
                asUnicode.CopyTo(to.Slice(bytesWritten));
                bytesWritten += asUnicode.Length;
                to[bytesWritten] = 0;
                bytesWritten++;
            }
            else
            {
                throw new ArgumentException("Unsupported type", value.GetType().Name);
            }
            
            // Write request and field name
            MemoryMarshal.Write(to, ref request);
            unicodeField.CopyTo(to.Slice(Marshal.SizeOf(request)));
            to[Marshal.SizeOf(request) + unicodeField.Length] = 0;
            
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write information about the file being printed
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="info">Information about the file being printed</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteFilePrintInfo(Span<byte> to, ParsedFileInfo info)
        {
            // Write header
            SetFilePrintInfo header = new SetFilePrintInfo
            {
                PrintTime = (uint)Math.Round(info.PrintTime),
                FileSize = (uint)info.Size,
                FirstLayerHeight = (float)info.FirstLayerHeight,
                LastModifiedTime = info.LastModified.HasValue ? (ulong)(info.LastModified.Value - new DateTime (1970, 1, 1)).TotalSeconds : 0,
                LayerHeight = (float)info.LayerHeight,
                NumFilaments = (uint)info.Filament.Length,
                ObjectHeight = (float)info.Height,
                SimulatedTime = (uint)Math.Round(info.SimulatedTime)
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf(header);
            
            // Write filaments
            foreach (double filament in info.Filament)
            {
                float filamentUsage = (float)filament;
                MemoryMarshal.Write(to.Slice(bytesWritten), ref filamentUsage);
                bytesWritten += Marshal.SizeOf(filamentUsage);
            }
            
            // Write filename
            Span<byte> unicodeFilename = Encoding.UTF8.GetBytes(info.FileName);
            unicodeFilename.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeFilename.Length;
            to[bytesWritten] = 0;
            bytesWritten++;
            
            // Write slicer
            Span<byte> unicodeGeneratedBy = Encoding.UTF8.GetBytes(info.GeneratedBy);
            unicodeGeneratedBy.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeGeneratedBy.Length;
            to[bytesWritten] = 0;
            bytesWritten++;
            
            return AddPadding(to, bytesWritten);
        }
        
        /// <summary>
        /// Write notification about a completed macro file
        /// </summary>
        /// <param name="span">Destination</param>
        /// <param name="channel">Channel where the macro file has finished</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteMacroCompleted(Span<byte> span, CodeChannel channel)
        {
            MacroCompleted header = new MacroCompleted
            {
                Channel = channel
            };
            MemoryMarshal.Write(span, ref header);
            return Marshal.SizeOf(header);
        }
        
        private static int AddPadding(Span<byte> to, int bytesWritten)
        {
            int padding = 4 - bytesWritten % 4;
            if (padding != 4)
            {
                to.Slice(bytesWritten, padding).Fill(0);
                return bytesWritten + padding;
            }

            return bytesWritten;
        }

    }
}