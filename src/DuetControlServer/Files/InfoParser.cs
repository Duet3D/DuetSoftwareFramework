using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Static class used to retrieve information from G-code jobs
    /// </summary>
    public static class InfoParser
    {
        /// <summary>
        /// Parse a G-code file
        /// </summary>
        /// <param name="fileName">File to analyze</param>
        /// <returns>Information about the file</returns>
        public static async Task<ParsedFileInfo> Parse(string fileName)
        {
            using FileStream fileStream = new FileStream(fileName, FileMode.Open);
            using StreamReader reader = new StreamReader(fileStream, null, true, Settings.FileBufferSize);
            ParsedFileInfo result = new ParsedFileInfo
            {
                FileName = await FilePath.ToVirtualAsync(fileName),
                LastModified = File.GetLastWriteTime(fileName),
                Size = fileStream.Length
            };

            if (fileStream.Length > 0)
            {
                await ParseHeader(reader, result);
                await ParseFooter(reader, result);
                await ParseThumbnails(reader, result);

                if (result.FirstLayerHeight + result.LayerHeight > 0F && result.Height > 0F)
                {
                    result.NumLayers = (int)Math.Round((result.Height - result.FirstLayerHeight) / result.LayerHeight) + 1;
                }
            }
            return result;
        }

        /// <summary>
        /// Parse the header of a G-code file
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="partialFileInfo">G-code file information</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ParseHeader(StreamReader reader, ParsedFileInfo partialFileInfo)
        {
            Code code = new Code();
            CodeParserBuffer codeParserBuffer = new CodeParserBuffer(Settings.FileBufferSize, true);

            bool inRelativeMode = false, lastCodeHadInfo = false, gotNewInfo = false;
            long fileReadLimit = Math.Min(Settings.FileInfoReadLimitHeader, reader.BaseStream.Length);
            while (codeParserBuffer.GetPosition(reader) < fileReadLimit)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();
                if (!await DuetAPI.Commands.Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }

                if (code.Type == CodeType.GCode && partialFileInfo.FirstLayerHeight == 0)
                {
                    if (code.MajorNumber == 91)
                    {
                        // G91 code (relative positioning)
                        inRelativeMode = true;
                        gotNewInfo = true;
                    }
                    else if (inRelativeMode)
                    {
                        // G90 (absolute positioning)
                        inRelativeMode = (code.MajorNumber != 90);
                        gotNewInfo = true;
                    }
                    else if (code.MajorNumber == 0 || code.MajorNumber == 1)
                    {
                        // G0/G1 is a move, see if there is a Z parameter present
                        CodeParameter zParam = code.Parameter('Z');
                        if (zParam != null)
                        {
                            float z = zParam;
                            if (z <= Settings.MaxLayerHeight)
                            {
                                partialFileInfo.FirstLayerHeight = z;
                                gotNewInfo = true;
                            }
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(code.Comment))
                {
                    gotNewInfo |= (partialFileInfo.LayerHeight == 0) && FindLayerHeight(code.Comment, ref partialFileInfo);
                    gotNewInfo |= FindFilamentUsed(code.Comment, ref partialFileInfo);
                    gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(code.Comment, ref partialFileInfo);
                    gotNewInfo |= (partialFileInfo.PrintTime == null) && FindPrintTime(code.Comment, ref partialFileInfo);
                    gotNewInfo |= (partialFileInfo.SimulatedTime == null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                }

                // Is the file info complete?
                if (!gotNewInfo && !lastCodeHadInfo && IsFileInfoComplete(partialFileInfo))
                {
                    break;
                }
                lastCodeHadInfo = gotNewInfo;
                code.Reset();
            }
        }

        /// <summary>
        /// Parse the footer of a G-code file
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="partialFileInfo">G-code file information</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ParseFooter(StreamReader reader, ParsedFileInfo partialFileInfo)
        {
            reader.BaseStream.Seek(0, SeekOrigin.End);
            char[] buffer = new char[Settings.FileBufferSize];
            int bufferPointer = 0;

            Code code = new Code();
            bool inRelativeMode = false, lastLineHadInfo = false, hadFilament = partialFileInfo.Filament.Count > 0;
            do
            {
                Program.CancellationToken.ThrowIfCancellationRequested();

                // Read another line
                ReadLineFromEndResult readResult = await ReadLineFromEndAsync(reader, buffer, bufferPointer);
                if (readResult == null)
                {
                    break;
                }
                bufferPointer = readResult.BufferPointer;

                // See what codes to deal with
                bool gotNewInfo = false;
                using (StringReader stringReader = new StringReader(readResult.Line))
                {
                    while (DuetAPI.Commands.Code.Parse(stringReader, code))
                    {
                        if (code.Type == CodeType.GCode && partialFileInfo.Height == 0)
                        {
                            if (code.MajorNumber == 90)
                            {
                                // G90 code (absolute positioning) implies we were in relative mode
                                inRelativeMode = true;
                                gotNewInfo = true;
                            }
                            else if (inRelativeMode)
                            {
                                // G91 code (relative positioning) implies we were in absolute mode
                                inRelativeMode = (code.MajorNumber != 91);
                                gotNewInfo = true;
                            }
                            else if (code.MajorNumber == 0 || code.MajorNumber == 1)
                            {
                                // G0/G1 is an absolute move, see if there is a Z parameter present
                                CodeParameter zParam = code.Parameter('Z');
                                if (zParam != null && (code.Comment == null || !code.Comment.TrimStart().StartsWith("E")))
                                {
                                    gotNewInfo = true;
                                    partialFileInfo.Height = zParam;
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(code.Comment))
                        {
                            gotNewInfo |= (partialFileInfo.LayerHeight == 0) && FindLayerHeight(code.Comment, ref partialFileInfo);
                            gotNewInfo |= !hadFilament && FindFilamentUsed(code.Comment, ref partialFileInfo);
                            gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(code.Comment, ref partialFileInfo);
                            gotNewInfo |= (partialFileInfo.PrintTime == null) && FindPrintTime(code.Comment, ref partialFileInfo);
                            gotNewInfo |= (partialFileInfo.SimulatedTime == null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                        }

                        // Prepare to read the next code
                        code.Reset();
                    }
                }

                // Is the file info complete?
                if (!gotNewInfo && !lastLineHadInfo && IsFileInfoComplete(partialFileInfo))
                {
                    break;
                }
                lastLineHadInfo = gotNewInfo;
            }
            while (reader.BaseStream.Length - reader.BaseStream.Position < Settings.FileInfoReadLimitFooter + buffer.Length);
        }

        /// <summary>
        /// Result for wrapping the buffer pointer because ref parameters are not supported for async functions
        /// </summary>
        private class ReadLineFromEndResult
        {
            /// <summary>
            /// Read line
            /// </summary>
            public string Line;

            /// <summary>
            /// New pointer in the buffer
            /// </summary>
            public int BufferPointer;
        }

        /// <summary>
        /// Read another line from the end of a file
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="buffer">Internal buffer</param>
        /// <param name="bufferPointer">Pointer to the next byte in the buffer</param>
        /// <returns>Read result</returns>
        private static async Task<ReadLineFromEndResult> ReadLineFromEndAsync(StreamReader reader, char[] buffer, int bufferPointer)
        {
            string line = string.Empty;
            do
            {
                if (bufferPointer == 0)
                {
                    if (reader.BaseStream.Position == 0)
                    {
                        return null;
                    }

                    reader.DiscardBufferedData();
                    if (reader.BaseStream.Position < buffer.Length)
                    {
                        int prevPosition = (int)reader.BaseStream.Position;
                        reader.BaseStream.Seek(0, SeekOrigin.Begin);
                        await reader.ReadBlockAsync(buffer);
                        bufferPointer = prevPosition;
                        reader.BaseStream.Seek(0, SeekOrigin.Begin);
                    }
                    else
                    {
                        long position = reader.BaseStream.Position - buffer.Length;
                        reader.BaseStream.Seek(position, SeekOrigin.Begin);
                        bufferPointer = await reader.ReadBlockAsync(buffer);
                        reader.BaseStream.Seek(position, SeekOrigin.Begin);
                    }
                }

                char c = buffer[--bufferPointer];
                if (c == '\n' || line.Length > buffer.Length)
                {
                    return new ReadLineFromEndResult
                    {
                        Line = line,
                        BufferPointer = bufferPointer
                    };
                }
                if (c != '\0' && c != '\r')
                {
                    line = c + line;
                }

                Program.CancellationToken.ThrowIfCancellationRequested();
            }
            while (true);
        }

        /// <summary>
        /// Checks if the given file info is complete
        /// </summary>
        /// <param name="result">File information</param>
        /// <returns>Whether the file info is complete</returns>
        private static bool IsFileInfoComplete(ParsedFileInfo result)
        {
            // Don't check PrintTime and SimulatedTime here because they are usually parsed before the following
            return (result.Height != 0) &&
                    (result.FirstLayerHeight != 0) &&
                    (result.LayerHeight != 0) &&
                    (result.Filament.Count > 0) &&
                    (!string.IsNullOrEmpty(result.GeneratedBy));
        }

        /// <summary>
        /// Try to find the layer height
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether layer height could be found</returns>
        private static bool FindLayerHeight(string line, ref ParsedFileInfo fileInfo)
        {
            foreach (Regex item in Settings.LayerHeightFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    foreach (Group grp in match.Groups)
                    {
                        if (grp.Name == "mm" && float.TryParse(grp.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float layerHeight) &&
                            float.IsFinite(layerHeight) && layerHeight < Settings.MaxLayerHeight)
                        {
                            fileInfo.LayerHeight = layerHeight;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Try to find the filament usage
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether filament consumption could be found</returns>
        private static bool FindFilamentUsed(string line, ref ParsedFileInfo fileInfo)
        {
            bool hadMatch = false;
            foreach (Regex item in Settings.FilamentFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    foreach (Group grp in match.Groups)
                    {
                        if (grp.Name == "mm")
                        {
                            foreach (Capture c in grp.Captures)
                            {
                                if (float.TryParse(c.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                                    float.IsFinite(filamentUsage))
                                {
                                    fileInfo.Filament.Add(filamentUsage);
                                }
                            }
                            hadMatch = true;
                        }
                        else if (grp.Name == "m")
                        {
                            foreach (Capture c in grp.Captures)
                            {
                                if (float.TryParse(c.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                                    float.IsFinite(filamentUsage))
                                {
                                    fileInfo.Filament.Add(filamentUsage * 1000F);
                                }
                            }
                            hadMatch = true;
                        }
                    }
                }
            }
            return hadMatch;
        }

        /// <summary>
        /// Find the toolchain that generated the file
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether the slicer could be found</returns>
        private static bool FindGeneratedBy(string line, ref ParsedFileInfo fileInfo)
        {
            foreach (Regex item in Settings.GeneratedByFilters)
            {
                Match match = item.Match(line);
                if (match.Success && match.Groups.Count > 1)
                {
                    fileInfo.GeneratedBy = match.Groups[1].Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Find the total print time
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether the print time could be found</returns>
        private static bool FindPrintTime(string line, ref ParsedFileInfo fileInfo)
        {
            foreach (Regex item in Settings.PrintTimeFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    long seconds = 0;
                    foreach (Group grp in match.Groups)
                    {
                        if (long.TryParse(grp.Value, out long printTime))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    seconds += printTime * 3600;
                                    break;
                                case "m":
                                    seconds += printTime * 60;
                                    break;
                                case "s":
                                    seconds += printTime;
                                    break;
                            }
                        }
                    }
                    if (seconds > 0)
                    {
                        fileInfo.PrintTime = seconds;
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Find the simulated time
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether the simulated time could be found</returns>
        private static bool FindSimulatedTime(string line, ref ParsedFileInfo fileInfo)
        {
            foreach (Regex item in Settings.SimulatedTimeFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    long seconds = 0;
                    foreach (Group grp in match.Groups)
                    {
                        if (long.TryParse(grp.Value, out long simulatedTime))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    seconds += simulatedTime * 3600;
                                    break;
                                case "m":
                                    seconds += simulatedTime * 60;
                                    break;
                                case "s":
                                    seconds += simulatedTime;
                                    break;
                            }
                        }
                    }
                    if (seconds > 0)
                    {
                        fileInfo.SimulatedTime = seconds;
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Marker used by RepRapFirmware for simulation times at the end of a file
        /// </summary>
        private const string SimulatedTimeString = "\n; Simulated print time";

        /// <summary>
        /// Update the last simulation time in a job file
        /// </summary>
        /// <param name="filename">Path to the job file</param>
        /// <param name="totalSeconds">Total print or simulated time</param>
        /// <returns>Asynchronous task</returns>
        public static async Task UpdateSimulatedTime(string filename, int totalSeconds)
        {
            // Get the last modified datetime
            DateTime lastWriteTime = File.GetLastWriteTime(filename);

            // Update the simulated time in the file
            using (FileStream fileStream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                // Check if we need to truncate the file before the last simulated time
                bool truncate = false;
                Memory<byte> buffer = new byte[64];
                if (fileStream.Length >= buffer.Length)
                {
                    fileStream.Seek(-buffer.Length, SeekOrigin.End);
                    int bytesRead = await fileStream.ReadAsync(buffer), offset = 0;
                    if (bytesRead > 0)
                    {
                        string bufferString = Encoding.UTF8.GetString(buffer.Slice(0, bytesRead).Span);
                        int simulationMarkerPosition = bufferString.IndexOf(SimulatedTimeString);
                        if (simulationMarkerPosition >= 0)
                        {
                            offset = bytesRead - simulationMarkerPosition;
                            truncate = true;
                        }
                    }
                    fileStream.Seek(-offset, SeekOrigin.End);
                }

                // Write the simulated time
                using (StreamWriter writer = new StreamWriter(fileStream, leaveOpen: true))
                {
                    await writer.WriteLineAsync(SimulatedTimeString + ": " + totalSeconds.ToString());
                }

                // Truncate the file if necessary
                if (truncate)
                {
                    fileStream.SetLength(fileStream.Position);
                }
            }

            // Restore the last modified datetime
            File.SetLastWriteTime(filename, lastWriteTime);
        }
        /// Parse the file for thumbnails.
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="partialFileInfo">G-code file information</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ParseThumbnails(StreamReader reader, ParsedFileInfo parsedFileInfo)
        {
            Code code = new Code();
            CodeParserBuffer codeParserBuffer = new CodeParserBuffer(Settings.FileBufferSize, true);
            bool imageFound = false;
            int encodedLength = 0;
            StringBuilder encodedImage = new StringBuilder();
            reader.BaseStream.Seek(0, SeekOrigin.Begin);
            ParsedThumbnailInfo thumbnailInfo = null;
            while (codeParserBuffer.GetPosition(reader) < reader.BaseStream.Length)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();
                if (!await DuetAPI.Commands.Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }

                if (code.Type != CodeType.Comment)
                {
                    return;
                }

                if (string.IsNullOrEmpty(code.Comment))
                {
                    code.Reset();
                    continue;
                }

                if (code.Comment.Contains("thumbnail begin", StringComparison.InvariantCultureIgnoreCase))
                {
                    //Exit if we find another start tag before ending the previous image
                    if (imageFound)
                    {
                        return;
                    }
                    var thumbnailTokens = code.Comment.Trim().Split(' ');
                    //Stop processing since the thumbnail may be corrupt.
                    if (thumbnailTokens.Length != 4)
                    {
                        return;
                    }
                    var dimensions = thumbnailTokens[2].Split('x');
                    if (dimensions.Length != 2)
                    {
                        continue;
                    }
                    imageFound = true;

                    thumbnailInfo = new ParsedThumbnailInfo();
                    thumbnailInfo.Width = int.Parse(dimensions[0]);
                    thumbnailInfo.Height = int.Parse(dimensions[1]);

                    encodedLength = int.Parse(thumbnailTokens[3]);
                    encodedImage.Clear();
                    code.Reset();
                    continue;
                }
                else if (code.Comment.Contains("thumbnail end", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (encodedImage.Length == encodedLength)
                    {
                        thumbnailInfo.EncodedImage = encodedImage.ToString();
                        parsedFileInfo.Thumbnails.Add(thumbnailInfo);
                    }
                    thumbnailInfo = null;
                    imageFound = false;
                }
                else if (imageFound)
                {
                    encodedImage.Append(code.Comment.Trim());
                }
                code.Reset();
            }
        }
    }
}
