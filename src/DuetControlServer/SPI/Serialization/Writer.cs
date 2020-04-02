using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.LinuxRequests;
using DuetControlServer.SPI.Communication.Shared;
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
        /// Size of a packet header
        /// </summary>
        public static readonly int PacketHeaderSize = Marshal.SizeOf(typeof(PacketHeader));

        /// <summary>
        /// Initialize a transfer header
        /// </summary>
        /// <param name="header">Header reference to initialize</param>
        public static void InitTransferHeader(ref TransferHeader header)
        {
            header.FormatCode = Consts.FormatCode;
            header.NumPackets = 0;
            header.ProtocolVersion = Consts.ProtocolVersion;
            header.SequenceNumber = 0;
            header.DataLength = 0;
            header.ChecksumData = 0;
            header.ChecksumHeader = 0;
        }

        /// <summary>
        /// Write an arbitrary packet header to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="request">Packet type</param>
        /// <param name="id">Packet ID</param>
        /// <param name="length">Length of the packet</param>
        public static void WritePacketHeader(Span<byte> to, Request request, ushort id, int length)
        {
            PacketHeader header = new PacketHeader()
            {
                Request = (ushort)request,
                Id = id,
                Length = (ushort)length,
                ResendPacketId = 0
            };
            MemoryMarshal.Write(to, ref header);
        }
        
        /// <summary>
        /// Write a parsed G/M/T code in binary format to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="code">Code to write</param>
        /// <returns>Number of bytes written</returns>
        /// <exception cref="ArgumentException">Unsupported data type</exception>
        public static int WriteCode(Span<byte> to, Code code)
        {
            int bytesWritten = 0;

            // Write code header
            CodeHeader header = new CodeHeader
            {
                Channel = code.Channel,
                FilePosition = (uint)(code.FilePosition ?? 0xFFFFFFFF),
                Letter = (byte)code.Type,
                MajorCode = code.MajorNumber ?? -1,
                MinorCode = code.MinorNumber ?? -1,
                NumParameters = (byte)code.Parameters.Count
            };

            if (code.MajorNumber != null)
            {
                header.Flags |= CodeFlags.HasMajorCommandNumber;
            }
            if (code.MinorNumber != null)
            {
                header.Flags |= CodeFlags.HasMinorCommandNumber;
            }
            if (code.FilePosition != null)
            {
                header.Flags |= CodeFlags.HasFilePosition;
            }
            if (code.Flags.HasFlag(DuetAPI.Commands.CodeFlags.EnforceAbsolutePosition))
            {
                header.Flags |= CodeFlags.EnforceAbsolutePosition;
            }

            MemoryMarshal.Write(to, ref header);
            bytesWritten += Marshal.SizeOf(header);

            // Write line number
            if (DataTransfer.ProtocolVersion >= 2)
            {
                int lineNumber = (int)(code.LineNumber ?? 0);
                MemoryMarshal.Write(to.Slice(bytesWritten), ref lineNumber);
                bytesWritten += Marshal.SizeOf<int>();
            }
            
            // Write parameters
            List<object> extraParameters = new List<object>();
            foreach (var parameter in code.Parameters)
            {
                CodeParameter binaryParam = new CodeParameter
                {
                    Letter = (byte)parameter.Letter
                };
                if (parameter.Type == typeof(int))
                {
                    binaryParam.Type = DataType.Int;
                    binaryParam.IntValue = parameter;
                }
                else if (parameter.Type == typeof(uint))
                {
                    binaryParam.Type = DataType.UInt;
                    binaryParam.UIntValue = parameter;
                }
                else if (parameter.Type == typeof(DriverId))
                {
                    binaryParam.Type = DataType.DriverId;
                    binaryParam.UIntValue = parameter;
                }
                else if (parameter.Type == typeof(float))
                {
                    binaryParam.Type = DataType.Float;
                    binaryParam.FloatValue = parameter;
                }
                else if (parameter.Type == typeof(int[]))
                {
                    binaryParam.Type = DataType.IntArray;
                    int[] array = parameter;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(uint[]))
                {
                    binaryParam.Type = DataType.UIntArray;
                    uint[] array = parameter;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(DriverId[]))
                {
                    binaryParam.Type = DataType.DriverIdArray;
                    DriverId[] array = parameter;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(float[]))
                {
                    binaryParam.Type = DataType.FloatArray;
                    float[] array = parameter;
                    binaryParam.IntValue = array.Length;
                    extraParameters.Add(array);
                }
                else if (parameter.Type == typeof(string))
                {
                    string value = parameter;
                    binaryParam.Type = parameter.IsExpression ? DataType.Expression : DataType.String;
                    binaryParam.IntValue = value.Length;
                    extraParameters.Add(value);
                }
                // Boolean values are not supported for codes. Use integers instead
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
                else if (parameter is DriverId[] driverIdArray)
                {
                    foreach (DriverId val in driverIdArray)
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
                    bytesWritten = AddPadding(to, bytesWritten);
                }
                else
                {
                    throw new ArgumentException("Unsupported type", parameter.GetType().Name);
                }
            }

            return bytesWritten;
        }

        /// <summary>
        /// Request a part of the object model
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="key">Key to the object model part</param>
        /// <param name="flags">Object model flags</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteGetObjectModel(Span<byte> to, string key, string flags)
        {
            Span<byte> unicodeKey = Encoding.UTF8.GetBytes(key);
            Span<byte> unicodeFlags = Encoding.UTF8.GetBytes(flags);

            // Write header
            GetObjectModelHeader request = new GetObjectModelHeader
            {
                KeyLength = (ushort)unicodeKey.Length,
                FlagsLength = (ushort)unicodeFlags.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf(request);

            // Write key
            unicodeKey.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeKey.Length;

            // Write flags
            unicodeFlags.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeFlags.Length;
            return AddPadding(to, bytesWritten);
        }
        
        /// <summary>
        /// Request the update of an object model field to an arbitrary value via a <see cref="Request.SetObjectModel"/> request
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="field">Path to the object model field</param>
        /// <param name="value">New value</param>
        /// <returns>Number of bytes written</returns>
        /// <exception cref="ArgumentException">Unsupported data type</exception>
        /// <remarks>value must be of type <see cref="DataType"/></remarks>
        public static int WriteSetObjectModel(Span<byte> to, string field, object value)
        {
            Span<byte> unicodeField = Encoding.UTF8.GetBytes(field);
            if (unicodeField.Length > 254)
            {
                throw new ArgumentException("Value is too long", nameof(field));
            }
            
            // First the header followed by the field path plus optional padding
            SetObjectModelHeader request = new SetObjectModelHeader
            {
                FieldLength = (byte)unicodeField.Length
            };
            int bytesWritten = AddPadding(to, Marshal.SizeOf(request) + unicodeField.Length);
            
            // Then the object value
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
                    bytesWritten += Marshal.SizeOf<int>();
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
                    bytesWritten += Marshal.SizeOf<uint>();
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
                    bytesWritten += Marshal.SizeOf<float>();
                }
            }
            else if (value is string stringValue)
            {
                Span<byte> asUnicode = Encoding.UTF8.GetBytes(stringValue);
                request.Type = DataType.String;
                request.IntValue = asUnicode.Length;
                asUnicode.CopyTo(to.Slice(bytesWritten));
                bytesWritten += asUnicode.Length;
                bytesWritten = AddPadding(to, bytesWritten);
            }
            else if (value is DriverId driverIdValue)
            {
                request.Type = DataType.UInt;
                request.UIntValue = driverIdValue;
            }
            else if (value is DriverId[] driverIdArray)
            {
                request.Type = DataType.DriverIdArray;
                request.IntValue = driverIdArray.Length;
                foreach (DriverId val in driverIdArray)
                {
                    uintValue = val;
                    MemoryMarshal.Write(to.Slice(bytesWritten), ref uintValue);
                    bytesWritten += Marshal.SizeOf<uint>();
                }
            }
            else if (value is bool boolValue)
            {
                request.Type = DataType.Bool;
                request.IntValue = Convert.ToInt32(boolValue);
            }
            else if (value is bool[] boolArray)
            {
                request.Type = DataType.BoolArray;
                request.IntValue = boolArray.Length;
                foreach (bool val in boolArray)
                {
                    byte byteVal = Convert.ToByte(val);
                    MemoryMarshal.Write(to.Slice(bytesWritten), ref byteVal);
                    bytesWritten += Marshal.SizeOf<byte>();
                }
            }
            else
            {
                throw new ArgumentException("Unsupported type", value.GetType().Name);
            }
            
            // Write request and field name
            MemoryMarshal.Write(to, ref request);
            unicodeField.CopyTo(to.Slice(Marshal.SizeOf(request)));
            return bytesWritten;
        }

        /// <summary>
        /// Notify the firmware that a print has started
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="info">Information about the file being printed</param>
        /// <returns>Number of bytes written</returns>
        /// <exception cref="ArgumentException">One of the supplied values is too big</exception>
        public static int WritePrintStarted(Span<byte> to, ParsedFileInfo info)
        {
            Span<byte> unicodeFilename = Encoding.UTF8.GetBytes(info.FileName);
            if (unicodeFilename.Length > 254)
            {
                throw new ArgumentException("Value is too long", nameof(info.FileName));
            }

            Span<byte> unicodeGeneratedBy = Encoding.UTF8.GetBytes(info.GeneratedBy);
            if (unicodeGeneratedBy.Length > 254)
            {
                throw new ArgumentException("Value is too long", nameof(info.GeneratedBy));
            }

            // Write header
            PrintStartedHeader header = new PrintStartedHeader
            {
                FilenameLength = (byte)unicodeFilename.Length,
                GeneratedByLength = (byte)unicodeGeneratedBy.Length,
                NumFilaments = (ushort)info.Filament.Count,
                FileSize = (uint)info.Size,
                LastModifiedTime = (info.LastModified != null) ? (ulong)(info.LastModified.Value - new DateTime(1970, 1, 1)).TotalSeconds : 0,
                FirstLayerHeight = info.FirstLayerHeight,
                LayerHeight = info.LayerHeight,
                ObjectHeight = info.Height,
                PrintTime = (uint)(info.PrintTime ?? 0),
                SimulatedTime = (uint)(info.SimulatedTime ?? 0)
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf(header);
            
            // Write filaments
            foreach (float filament in info.Filament)
            {
                float filamentUsage = filament;
                MemoryMarshal.Write(to.Slice(bytesWritten), ref filamentUsage);
                bytesWritten += Marshal.SizeOf<float>();
            }
            
            // Write filename
            unicodeFilename.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeFilename.Length;
            
            // Write slicer
            unicodeGeneratedBy.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeGeneratedBy.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Notify the firmware that a print has been stopped
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="reason">Reason why the print has been stopped</param>
        /// <returns>Number of bytes written</returns>
        public static int WritePrintStopped(Span<byte> to, PrintStoppedReason reason)
        {
            PrintStoppedHeader header = new PrintStoppedHeader
            {
                Reason = reason
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Write notification about a completed macro file
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Channel where the macro file has finished</param>
        /// <param name="error">Whether an error occurred when trying to open/process the macro file</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteMacroCompleted(Span<byte> to, CodeChannel channel, bool error)
        {
            MacroCompleteHeader header = new MacroCompleteHeader
            {
                Channel = channel,
                Error = (byte)(error ? 1 : 0)
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Write a heightmap as read by G29 S1
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="map">Heightmap to write</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteHeightMap(Span<byte> to, Heightmap map)
        {
            HeightMapHeader header = new HeightMapHeader
            {
                XMin = map.XMin,
                XMax = map.XMax,
                XSpacing = map.XSpacing,
                YMin = map.YMin,
                YMax = map.YMax,
                YSpacing = map.YSpacing,
                Radius = map.Radius,
                NumX = (ushort)map.NumX,
                NumY = (ushort)map.NumY
            };
            MemoryMarshal.Write(to, ref header);

            Span<float> coords = MemoryMarshal.Cast<byte, float>(to.Slice(Marshal.SizeOf(header)));
            for (int i = 0; i < map.NumX * map.NumY; i++)
            {
                coords[i] = map.ZCoordinates[i];
            }

            return Marshal.SizeOf(header) + Marshal.SizeOf<float>() * map.NumX * map.NumY;
        }

        /// <summary>
        /// Write a G-code channel
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Channel for the lock request</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteCodeChannel(Span<byte> to, CodeChannel channel)
        {
            CodeChannelHeader header = new CodeChannelHeader
            {
                Channel = channel
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf(header);
        }

        /// <summary>
        /// Assign a filament name to the given extruder drive
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="extruder">Extruder drive</param>
        /// <param name="filamentName">Filament name</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteAssignFilament(Span<byte> to, int extruder, string filamentName)
        {
            Span<byte> unicodeFilamentName = Encoding.UTF8.GetBytes(filamentName);
            if (unicodeFilamentName.Length > 32)
            {
                throw new ArgumentException("Value is too long", nameof(filamentName));
            }

            // Write header
            AssignFilamentHeader header = new AssignFilamentHeader
            {
                Extruder = extruder,
                FilamentLength = (uint)unicodeFilamentName.Length
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf(header);

            // Write filament name
            unicodeFilamentName.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeFilamentName.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a file chunk
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="data">File chunk data</param>
        /// <param name="fileLength">Total length of the file in bytes</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteFileChunk(Span<byte> to, Span<byte> data, long fileLength)
        {
            // Write header
            FileChunk header = new FileChunk
            {
                DataLength = (data != null) ? data.Length : -1,
                FileLength = (uint)fileLength
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf(header);

            // Write chunk
            if (data != null)
            {
                data.CopyTo(to.Slice(bytesWritten));
                bytesWritten += data.Length;
            }
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a request to evaluate an expression
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteEvaluateExpression(Span<byte> to, CodeChannel channel, string expression)
        {
            Span<byte> unicodeExpression = Encoding.UTF8.GetBytes(expression);

            // Write header
            CodeChannelHeader header = new CodeChannelHeader
            {
                Channel = channel
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf(header);

            // Write expression
            unicodeExpression.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeExpression.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a <see cref="StringHeader"/> to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="data">String data</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteStringRequest(Span<byte> to, string data)
        {
            Span<byte> unicodeData = Encoding.UTF8.GetBytes(data);

            // Write header
            StringHeader request = new StringHeader
            {
                Length = (ushort)unicodeData.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf(request);

            // Write data
            unicodeData.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeData.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a <see cref="MessageHeader"/> to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="type">Message flags</param>
        /// <param name="message">Message content</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteMessage(Span<byte> to, MessageTypeFlags type, string message)
        {
            Span<byte> unicodeMessage = Encoding.UTF8.GetBytes(message);

            // Write header
            MessageHeader request = new MessageHeader
            {
                MessageType = type,
                Length = (ushort)unicodeMessage.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf(request);

            // Write data
            unicodeMessage.CopyTo(to.Slice(bytesWritten));
            bytesWritten += unicodeMessage.Length;
            return AddPadding(to, bytesWritten);
        }
        /// <summary>
        /// Add padding bytes after a packet to maintain proper alignment
        /// </summary>
        /// <param name="to">Target buffer</param>
        /// <param name="bytesWritten">Number of bytes written so far</param>
        /// <returns>New pointer in the buffer</returns>
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