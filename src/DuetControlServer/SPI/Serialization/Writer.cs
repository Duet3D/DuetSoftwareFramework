using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.LinuxRequests;
using DuetControlServer.SPI.Communication.Shared;
using Code = DuetControlServer.Commands.Code;
using CodeFlags = DuetControlServer.SPI.Communication.LinuxRequests.CodeFlags;
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
            header.ChecksumData32 = 0;
            header.ChecksumHeader32 = 0;
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
            PacketHeader header = new()
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
            CodeHeader header = new()
            {
                Channel = code.Channel,
                FilePosition = (uint)(code.FilePosition ?? 0xFFFFFFFF),
                Letter = (byte)code.Type,
                MajorCode = (code.Type == CodeType.Comment) ? 0 : (code.MajorNumber ?? -1),
                MinorCode = code.MinorNumber ?? -1,
                NumParameters = (byte)((code.Type == CodeType.Comment) ? 1 : code.Parameters.Count)
            };

            if (code.Type == CodeType.Comment || code.MajorNumber != null)
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
            bytesWritten += Marshal.SizeOf<CodeHeader>();

            // Write line number
            if (DataTransfer.ProtocolVersion >= 2)
            {
                int lineNumber = (int)(code.LineNumber ?? 0);
                MemoryMarshal.Write(to[bytesWritten..], ref lineNumber);
                bytesWritten += sizeof(int);
            }

            // Write parameters
            if (code.Type == CodeType.Comment)
            {
                // Write comment as an unprecedented parameter
                string comment = (code.Comment ?? string.Empty).Trim();
                int commentLength = Math.Min(comment.Length, Consts.MaxCommentLength);
                CodeParameter binaryParam = new()
                {
                    Letter = (byte)'@',
                    IntValue = commentLength,
                    Type = DataType.String
                };
                MemoryMarshal.Write(to[bytesWritten..], ref binaryParam);
                bytesWritten += Marshal.SizeOf<CodeParameter>();

                Span<byte> asUnicode = Encoding.UTF8.GetBytes(comment.Substring(0, commentLength));
                asUnicode.CopyTo(to[bytesWritten..]);
                bytesWritten += asUnicode.Length;
                bytesWritten = AddPadding(to, bytesWritten);
            }
            else
            {
                // Write code parameters
                List<object> extraParameters = new();
                foreach (var parameter in code.Parameters)
                {
                    CodeParameter binaryParam = new()
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
                        binaryParam.IntValue = Encoding.UTF8.GetByteCount(value);
                        extraParameters.Add(value);
                    }
                    // Boolean values are not supported for codes. Use integers instead
                    else
                    {
                        throw new ArgumentException("Unsupported type", parameter.Type.Name);
                    }

                    MemoryMarshal.Write(to[bytesWritten..], ref binaryParam);
                    bytesWritten += Marshal.SizeOf<CodeParameter>();
                }

                // Write extra parameters
                foreach (object parameter in extraParameters)
                {
                    if (parameter is int[] intArray)
                    {
                        foreach (int val in intArray)
                        {
                            int value = val;
                            MemoryMarshal.Write(to[bytesWritten..], ref value);
                            bytesWritten += sizeof(int);
                        }
                    }
                    else if (parameter is uint[] uintArray)
                    {
                        foreach (uint val in uintArray)
                        {
                            uint value = val;
                            MemoryMarshal.Write(to[bytesWritten..], ref value);
                            bytesWritten += sizeof(uint);
                        }
                    }
                    else if (parameter is DriverId[] driverIdArray)
                    {
                        foreach (DriverId val in driverIdArray)
                        {
                            uint value = val;
                            MemoryMarshal.Write(to[bytesWritten..], ref value);
                            bytesWritten += sizeof(uint);
                        }
                    }
                    else if (parameter is float[] floatArray)
                    {
                        foreach (float val in floatArray)
                        {
                            float value = val;
                            MemoryMarshal.Write(to[bytesWritten..], ref value);
                            bytesWritten += sizeof(float);
                        }
                    }
                    else if (parameter is string value)
                    {
                        Span<byte> asUnicode = Encoding.UTF8.GetBytes(value);
                        asUnicode.CopyTo(to[bytesWritten..]);
                        bytesWritten += asUnicode.Length;
                        bytesWritten = AddPadding(to, bytesWritten);
                    }
                    else
                    {
                        throw new ArgumentException("Unsupported type", parameter.GetType().Name);
                    }
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
            GetObjectModelHeader request = new()
            {
                KeyLength = (ushort)unicodeKey.Length,
                FlagsLength = (ushort)unicodeFlags.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf<GetObjectModelHeader>();

            // Write key
            unicodeKey.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeKey.Length;

            // Write flags
            unicodeFlags.CopyTo(to[bytesWritten..]);
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
            SetObjectModelHeader request = new()
            {
                FieldLength = (byte)unicodeField.Length
            };
            int setObjectModelHeaderLength = Marshal.SizeOf<SetObjectModelHeader>();
            int bytesWritten = AddPadding(to, setObjectModelHeaderLength + unicodeField.Length);

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
                    MemoryMarshal.Write(to[bytesWritten..], ref intValue);
                    bytesWritten += sizeof(int);
                }
            }
            else if (value is uint[] uintArray)
            {
                request.Type = DataType.UIntArray;
                request.IntValue = uintArray.Length;
                foreach (uint val in uintArray)
                {
                    uintValue = val;
                    MemoryMarshal.Write(to[bytesWritten..], ref uintValue);
                    bytesWritten += sizeof(uint);
                }
            }
            else if (value is float[] floatArray)
            {
                request.Type = DataType.FloatArray;
                request.IntValue = floatArray.Length;
                foreach (float val in floatArray)
                {
                    floatValue = val;
                    MemoryMarshal.Write(to[bytesWritten..], ref floatValue);
                    bytesWritten += sizeof(float);
                }
            }
            else if (value is string stringValue)
            {
                Span<byte> asUnicode = Encoding.UTF8.GetBytes(stringValue);
                request.Type = DataType.String;
                request.IntValue = asUnicode.Length;
                asUnicode.CopyTo(to[bytesWritten..]);
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
                    MemoryMarshal.Write(to[bytesWritten..], ref uintValue);
                    bytesWritten += sizeof(uint);
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
                    MemoryMarshal.Write(to[bytesWritten..], ref byteVal);
                    bytesWritten += sizeof(byte);
                }
            }
            else
            {
                throw new ArgumentException("Unsupported type", value.GetType().Name);
            }
            
            // Write request and field name
            MemoryMarshal.Write(to, ref request);
            unicodeField.CopyTo(to[setObjectModelHeaderLength..]);
            return bytesWritten;
        }

        /// <summary>
        /// Notify the firmware that a print has started
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="info">Information about the file being printed</param>
        /// <returns>Number of bytes written</returns>
        /// <exception cref="ArgumentException">One of the supplied values is too big</exception>
        public static int WritePrintFileInfo(Span<byte> to, ParsedFileInfo info)
        {
            Span<byte> unicodeFilename = Encoding.UTF8.GetBytes(info.FileName);
            if (unicodeFilename.Length > 254)
            {
                throw new ArgumentException("Filename is too long", nameof(info));
            }

            Span<byte> unicodeGeneratedBy = Encoding.UTF8.GetBytes(info.GeneratedBy ?? string.Empty);
            if (unicodeGeneratedBy.Length > 254)
            {
                throw new ArgumentException("GeneratedBy is too long", nameof(info));
            }

            // Write header
            PrintStartedHeader header = new()
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
            int bytesWritten = Marshal.SizeOf<PrintStartedHeader>();
            
            // Write filaments
            foreach (float filament in info.Filament)
            {
                float filamentUsage = filament;
                MemoryMarshal.Write(to[bytesWritten..], ref filamentUsage);
                bytesWritten += sizeof(float);
            }
            
            // Write filename
            unicodeFilename.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeFilename.Length;
            
            // Write slicer
            unicodeGeneratedBy.CopyTo(to[bytesWritten..]);
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
            PrintStoppedHeader header = new()
            {
                Reason = reason
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf<PrintStoppedHeader>();
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
            MacroCompleteHeader header = new()
            {
                Channel = channel,
                Error = (byte)(error ? 1 : 0)
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf<MacroCompleteHeader>();
        }

        /// <summary>
        /// Write a heightmap as read by G29 S1
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="map">Heightmap to write</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteHeightMap(Span<byte> to, Heightmap map)
        {
            HeightMapHeader header = new()
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

            Span<float> coords = MemoryMarshal.Cast<byte, float>(to[Marshal.SizeOf<HeightMapHeader>()..]);
            for (int i = 0; i < map.NumX * map.NumY; i++)
            {
                coords[i] = map.ZCoordinates[i];
            }

            return Marshal.SizeOf<HeightMapHeader>() + Marshal.SizeOf<float>() * map.NumX * map.NumY;
        }

        /// <summary>
        /// Write a G-code channel
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Channel for the lock request</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteCodeChannel(Span<byte> to, CodeChannel channel)
        {
            CodeChannelHeader header = new()
            {
                Channel = channel
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf<CodeChannelHeader>();
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
            AssignFilamentHeader header = new()
            {
                Extruder = extruder,
                FilamentLength = (uint)unicodeFilamentName.Length
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf<AssignFilamentHeader>();

            // Write filament name
            unicodeFilamentName.CopyTo(to[bytesWritten..]);
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
            FileChunkHeader header = new()
            {
                DataLength = (data != null) ? data.Length : -1,
                FileLength = (uint)fileLength
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf<FileChunkHeader>();

            // Write chunk
            if (data != null)
            {
                data.CopyTo(to[bytesWritten..]);
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
            if (unicodeExpression.Length > Consts.MaxExpressionLength)
            {
                throw new ArgumentException("Value is too long", nameof(expression));
            }

            // Write header
            CodeChannelHeader header = new()
            {
                Channel = channel
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf<CodeChannelHeader>();

            // Write expression
            unicodeExpression.CopyTo(to[bytesWritten..]);
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
            StringHeader request = new()
            {
                Length = (ushort)unicodeData.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf<StringHeader>();

            // Write data
            unicodeData.CopyTo(to[bytesWritten..]);
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
            MessageHeader request = new()
            {
                MessageType = type,
                Length = (ushort)unicodeMessage.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf<MessageHeader>();

            // Write data
            unicodeMessage.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeMessage.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a <see cref="SetVariableHeader"/> to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Source of this request</param>
        /// <param name="createVariable">Create a new variable</param>
        /// <param name="varName">Name of the variable including prefix</param>
        /// <param name="expression">Content to assign to the variable</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteSetVariable(Span<byte> to, CodeChannel channel, bool createVariable, string varName, string expression)
        {
            Span<byte> unicodeVarName = Encoding.UTF8.GetBytes(varName);
            if (unicodeVarName.Length > Consts.MaxVariableLength)
            {
                throw new ArgumentException("Value is too long", nameof(varName));
            }

            Span<byte> unicodeExpression = Encoding.UTF8.GetBytes(expression);
            if (unicodeExpression.Length > Consts.MaxExpressionLength)
            {
                throw new ArgumentException("Value is too long", nameof(expression));
            }

            // Write header
            SetVariableHeader request = new()
            {
                Channel = channel,
                CreateVariable = (byte)(createVariable ? 1 : 0),
                VariableLength = (byte)unicodeVarName.Length,
                ExpressionLength = (byte)unicodeExpression.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf<SetVariableHeader>();

            // Write variable name
            unicodeVarName.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeVarName.Length;

            // Write expression
            unicodeExpression.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeExpression.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write a <see cref="DeleteLocalVariableHeader"/> to a memory span
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="channel">Source of this request</param>
        /// <param name="varName">Name of the variable excluding var prefix</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteDeleteLocalVariable(Span<byte> to, CodeChannel channel, string varName)
        {
            Span<byte> unicodeVarName = Encoding.UTF8.GetBytes(varName);
            if (unicodeVarName.Length > Consts.MaxVariableLength)
            {
                throw new ArgumentException("Value is too long", nameof(varName));
            }

            // Write header
            DeleteLocalVariableHeader request = new()
            {
                Channel = channel,
                VariableLength = (byte)unicodeVarName.Length
            };
            MemoryMarshal.Write(to, ref request);
            int bytesWritten = Marshal.SizeOf<DeleteLocalVariableHeader>();

            // Write variable name
            unicodeVarName.CopyTo(to[bytesWritten..]);
            bytesWritten += unicodeVarName.Length;
            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Write an arbitrary boolean value
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="exists"></param>
        /// <returns>Number of bytes written</returns>
        public static int WriteBoolean(Span<byte> to, bool value)
        {
            BooleanHeader header = new()
            {
                Value = Convert.ToByte(value)
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf<BooleanHeader>();
        }

        /// <summary>
        /// Write the result of an attempt to open a file
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="handle">File handle</param>
        /// <param name="fileSize">File length</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteOpenFileResult(Span<byte> to, uint handle, long fileSize)
        {
            OpenFileResult header = new()
            {
                Handle = handle,
                FileSize = (uint)fileSize
            };
            MemoryMarshal.Write(to, ref header);
            return Marshal.SizeOf<OpenFileResult>();
        }

        /// <summary>
        /// Write read file data
        /// </summary>
        /// <param name="to">Destination</param>
        /// <param name="data">Read file data</param>
        /// <param name="bytesRead">Number of bytes read</param>
        /// <returns>Number of bytes written</returns>
        public static int WriteFileReadResult(Span<byte> to, Span<byte> data, int bytesRead)
        {
            // Write header
            FileDataHeader header = new()
            {
                BytesRead = bytesRead
            };
            MemoryMarshal.Write(to, ref header);
            int bytesWritten = Marshal.SizeOf<FileDataHeader>();

            // Write content
            data.CopyTo(to[bytesWritten..]);
            bytesWritten += data.Length;

            return AddPadding(to, bytesWritten);
        }

        /// <summary>
        /// Add padding bytes to maintain alignment on a 4-byte boundary
        /// </summary>
        /// <param name="to">Target buffer</param>
        /// <param name="bytesWritten">Number of bytes written so far</param>
        /// <returns>Aligned number of bytes</returns>
        private static int AddPadding(Span<byte> to, int bytesWritten)
        {
            int extraBytes = bytesWritten & 3;
            if (extraBytes == 0)
            {
                return bytesWritten;
            }

            int bytesTotal = bytesWritten + 4 - extraBytes;
            to[bytesWritten..bytesTotal].Fill(0);
            return bytesTotal;
        }
    }
}