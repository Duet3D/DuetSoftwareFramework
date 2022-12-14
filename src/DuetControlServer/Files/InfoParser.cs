using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files.ImageProcessing;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Static class used to retrieve information from G-code jobs
    /// </summary>
    public static class InfoParser
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Parse a G-code file
        /// </summary>
        /// <param name="fileName">File to analyze</param>
        /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
        /// <returns>Information about the file</returns>
        public static async Task<GCodeFileInfo> Parse(string fileName, bool readThumbnailContent)
        {
            await using FileStream fileStream = new(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
            using StreamReader reader = new(fileStream, null, true, Settings.FileBufferSize);
            GCodeFileInfo result = new()
            {
                FileName = await FilePath.ToVirtualAsync(fileName),
                LastModified = File.GetLastWriteTime(fileName),
                Size = fileStream.Length
            };

            if (fileStream.Length > 0 && (
                    fileName.EndsWith(".gcode", StringComparison.InvariantCultureIgnoreCase) ||
                    fileName.EndsWith(".g", StringComparison.InvariantCultureIgnoreCase) ||
                    fileName.EndsWith(".gco", StringComparison.InvariantCultureIgnoreCase) ||
                    fileName.EndsWith(".gc", StringComparison.InvariantCultureIgnoreCase) ||
                    fileName.EndsWith(".nc", StringComparison.InvariantCultureIgnoreCase)
               ))
            {
                await ParseHeader(reader, readThumbnailContent, result);
                await ParseFooter(reader, result);

                while (result.Filament.Count > 0 && result.Filament[0] == 0F)
                {
                    // In case the filament index did not start at zero...
                    result.Filament.RemoveAt(0);
                }
                while (result.Filament.Count > 0 && result.Filament[^1] == 0F)
                {
                    // In case the last items were zero
                    result.Filament.RemoveAt(result.Filament.Count - 1);
                }

                if (result.NumLayers == 0 && result.LayerHeight > 0F && result.Height > 0F)
                {
                    result.NumLayers = (int)Math.Round(result.Height / result.LayerHeight);
                }
            }
            return result;
        }

        /// <summary>
        /// Parse the header of a G-code file
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
        /// <param name="partialFileInfo">G-code file information</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ParseHeader(StreamReader reader, bool readThumbnailContent, GCodeFileInfo partialFileInfo)
        {
            Code code = new();
            CodeParserBuffer codeParserBuffer = new(Settings.FileBufferSize, true);

            bool lastCodeHadInfo = false, gotNewInfo = false;
            long fileReadLimit = Math.Min(Settings.FileInfoReadLimitHeader, reader.BaseStream.Length);
            while (codeParserBuffer.GetPosition(reader) < fileReadLimit || gotNewInfo)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();

                gotNewInfo = false;
                if (!await DuetAPI.Commands.Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(code.Comment))
                {
                    gotNewInfo |= (partialFileInfo.SimulatedTime == null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                    gotNewInfo |= !gotNewInfo && (partialFileInfo.PrintTime == null) && FindPrintTime(code.Comment, ref partialFileInfo);
                    gotNewInfo |= (partialFileInfo.LayerHeight == 0) && FindLayerHeight(code.Comment, ref partialFileInfo);
                    gotNewInfo |= (partialFileInfo.NumLayers == 0) && FindNumLayers(code.Comment, ref partialFileInfo);
                    gotNewInfo |= FindFilamentUsed(code.Comment, ref partialFileInfo);
                    gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(code.Comment, ref partialFileInfo);
                    gotNewInfo |= await ParseThumbnails(reader, code, codeParserBuffer, partialFileInfo, readThumbnailContent);
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
        private static async Task ParseFooter(StreamReader reader, GCodeFileInfo partialFileInfo)
        {
            reader.BaseStream.Seek(0, SeekOrigin.End);
            ReadLineFromEndData readData = new(reader.BaseStream.Position);
            char[] buffer = new char[Settings.FileBufferSize];

            Code code = new();
            bool inRelativeMode = false, lastLineHadInfo = false, hadFilament = partialFileInfo.Filament.Count > 0;
            do
            {
                Program.CancellationToken.ThrowIfCancellationRequested();

                // Read another line
                if (!await ReadLineFromEndAsync(reader, buffer, readData))
                {
                    break;
                }

                // See what codes to deal with
                bool gotNewInfo = false;
                using (StringReader stringReader = new(readData.Line))
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
                                if (zParam != null && (zParam.Type == typeof(int) || zParam.Type == typeof(float)) &&
                                    (code.Comment == null || !code.Comment.TrimStart().StartsWith("E", StringComparison.InvariantCultureIgnoreCase)))
                                {
                                    gotNewInfo = true;
                                    partialFileInfo.Height = zParam;
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(code.Comment))
                        {
                            gotNewInfo |= (partialFileInfo.SimulatedTime == null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                            gotNewInfo |= !gotNewInfo && (partialFileInfo.PrintTime == null) && FindPrintTime(code.Comment, ref partialFileInfo);
                            gotNewInfo |= (partialFileInfo.LayerHeight == 0) && FindLayerHeight(code.Comment, ref partialFileInfo);
                            gotNewInfo |= (partialFileInfo.NumLayers == 0) && FindNumLayers(code.Comment, ref partialFileInfo);
                            gotNewInfo |= !hadFilament && FindFilamentUsed(code.Comment, ref partialFileInfo);
                            gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(code.Comment, ref partialFileInfo);
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
        private class ReadLineFromEndData
        {
            /// <summary>
            /// Read line
            /// </summary>
            public string Line;

            /// <summary>
            /// New pointer in the buffer
            /// </summary>
            public int BufferPointer;

            /// <summary>
            /// Last file position
            /// </summary>
            public long FilePosition;

            /// <summary>
            /// Constructor of this class
            /// </summary>
            /// <param name="filePosition">Current file position</param>
            public ReadLineFromEndData(long filePosition) => FilePosition = filePosition;
        }

        /// <summary>
        /// Read another line from the end of a file
        /// </summary>
        /// <param name="reader">Stream reader</param>
        /// <param name="buffer">Internal buffer</param>
        /// <param name="bufferPointer">Pointer to the next byte in the buffer</param>
        /// <returns>Read result</returns>
        private static async ValueTask<bool> ReadLineFromEndAsync(StreamReader reader, char[] buffer, ReadLineFromEndData readData)
        {
            string line = string.Empty;
            do
            {
                if (readData.BufferPointer == 0)
                {
                    if (readData.FilePosition == 0)
                    {
                        return false;
                    }

                    reader.DiscardBufferedData();
                    if (readData.FilePosition < buffer.Length)
                    {
                        reader.BaseStream.Seek(0, SeekOrigin.Begin);
                        readData.BufferPointer = Math.Min(await reader.ReadBlockAsync(buffer), (int)readData.FilePosition);
                        readData.FilePosition = 0;
                    }
                    else
                    {
                        readData.FilePosition -= buffer.Length;
                        reader.BaseStream.Seek(readData.FilePosition, SeekOrigin.Begin);
                        readData.BufferPointer = await reader.ReadBlockAsync(buffer);
                        readData.FilePosition += buffer.Length - readData.BufferPointer;    // ... in case the number of chars read is different from the buffer length
                    }
                }

                char c = buffer[--readData.BufferPointer];
                if (c == '\n' || line.Length > buffer.Length)
                {
                    readData.Line = line;
                    return true;
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
        private static bool IsFileInfoComplete(GCodeFileInfo result)
        {
            // Don't check PrintTime and SimulatedTime here because they are usually parsed before the following.
            // Also don't check for NumLayers because that is optional and can be computed from the object+layer heights
            return (result.Height != 0) &&
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
        private static bool FindLayerHeight(string line, ref GCodeFileInfo fileInfo)
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
        /// Try to find the total number of layers
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether number of layers could be found</returns>
        private static bool FindNumLayers(string line, ref GCodeFileInfo fileInfo)
        {
            foreach (Regex item in Settings.NumLayersFilters)
            {
                Match match = item.Match(line);
                if (match.Success && match.Groups.Count > 1)
                {
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int numLayers) && numLayers > 0)
                    {
                        fileInfo.NumLayers = numLayers;
                        return true;
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
        private static bool FindFilamentUsed(string line, ref GCodeFileInfo fileInfo)
        {
            foreach (Regex item in Settings.FilamentFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    if (match.Groups.TryGetValue("mm", out Group mmGroup))
                    {
                        if (float.TryParse(mmGroup.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                            float.IsFinite(filamentUsage))
                        {
                            if (match.Groups.TryGetValue("index", out Group indexGroup) && int.TryParse(indexGroup.Value, out int index))
                            {
                                for (int i = fileInfo.Filament.Count; i <= index; i++)
                                {
                                    fileInfo.Filament.Add(0F);
                                }
                                fileInfo.Filament[index] = filamentUsage;
                            }
                            else
                            {
                                foreach (Capture capture in mmGroup.Captures)
                                {
                                    if (float.TryParse(capture.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out filamentUsage) &&
                                        float.IsFinite(filamentUsage))
                                    {
                                        fileInfo.Filament.Add(filamentUsage);
                                    }
                                }
                            }
                        }
                        return true;
                    }

                    if (match.Groups.TryGetValue("m", out Group mGroup))
                    {
                        if (float.TryParse(mGroup.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                            float.IsFinite(filamentUsage))
                        {
                            if (match.Groups.TryGetValue("index", out Group indexGroup) && int.TryParse(indexGroup.Value, out int index))
                            {
                                for (int i = fileInfo.Filament.Count; i <= index; i++)
                                {
                                    fileInfo.Filament.Add(0F);
                                }
                                fileInfo.Filament[index] = filamentUsage * 1000F;
                            }
                            else
                            {
                                foreach (Capture capture in mGroup.Captures)
                                {
                                    if (float.TryParse(capture.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out filamentUsage) &&
                                        float.IsFinite(filamentUsage))
                                    {
                                        fileInfo.Filament.Add(filamentUsage * 1000F);
                                    }
                                }
                            }
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Find the toolchain that generated the file
        /// </summary>
        /// <param name="line">Line</param>
        /// <param name="fileInfo">File information</param>
        /// <returns>Whether the slicer could be found</returns>
        private static bool FindGeneratedBy(string line, ref GCodeFileInfo fileInfo)
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
        private static bool FindPrintTime(string line, ref GCodeFileInfo fileInfo)
        {
            foreach (Regex item in Settings.PrintTimeFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    long seconds = 0;
                    foreach (Group grp in match.Groups)
                    {
                        if (float.TryParse(grp.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float printTime) &&
                            float.IsFinite(printTime))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    seconds += (long)Math.Round(printTime) * 3600L;
                                    break;
                                case "m":
                                    seconds += (long)Math.Round(printTime)* 60L;
                                    break;
                                case "s":
                                    seconds += (long)Math.Round(printTime);
                                    break;
                            }
                        }
                    }
                    if (seconds > 0)
                    {
                        fileInfo.PrintTime = seconds;
                        return true;
                    }
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
        private static bool FindSimulatedTime(string line, ref GCodeFileInfo fileInfo)
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
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if the current code contains thumbnail data
        /// </summary>
        /// <param name="code">Code being parsed which must have a valid comment</param>
        /// <param name="reader">Stream reader</param>
        /// <param name="parsedFileInfo">G-code file information</param>
        /// <param name="codeParserBuffer">Parser buffer</param>
        /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
        /// <returns>True if the code contains thumbnail data</returns>
        private static async ValueTask<bool> ParseThumbnails(StreamReader reader, Code code, CodeParserBuffer codeParserBuffer, GCodeFileInfo parsedFileInfo, bool readThumbnailContent)
        {
            // This is the start of an embedded thumbnail image
            string trimmedComment = code.Comment.TrimStart();
            if (trimmedComment.StartsWith("thumbnail begin", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.Debug("Found embedded thumbnail PNG image");
                await ImageParser.ProcessAsync(reader, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.PNG);
                return true;
            }
            if (trimmedComment.StartsWith("thumbnail_JPG", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.Debug("Found embedded thumbnail JPG Image");
                await ImageParser.ProcessAsync(reader, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.JPEG);
                return true;
            }
            if (trimmedComment.StartsWith("thumbnail_QOI", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.Debug("Found embedded thumbnail QOI Image");
                await ImageParser.ProcessAsync(reader, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.QOI);
                return true;
            }

            // Icon Image (proprietary)
            if (trimmedComment.Contains("Icon:"))
            {
                _logger.Debug("Found Icon Image");
                await IconImageParser.ProcessAsync(reader, codeParserBuffer, parsedFileInfo, code, readThumbnailContent);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Maximum length of thumbnail data in a thumbnail response
        /// </summary>
        /// <remarks>
        /// See RepRapFirmware -> RepRap.cpp -> GetThumbnailResponse
        /// </remarks>
        private const int MaxThumbnailLength = 1024;

        /// <summary>
        /// Retrieve a chunk of a thumbnail for PanelDue compatibility
        /// </summary>
        /// <param name="filename">G-code file to parse</param>
        /// <param name="offset">File offset to start from</param>
        /// <returns>JSON response</returns>
        public static async ValueTask<string> ParseThumbnail(string filename, long offset)
        {
            StringBuilder jsonResult = new();
            jsonResult.Append("{\"thumbnail\":{\"fileName\":");
            jsonResult.Append(JsonSerializer.Serialize(await FilePath.ToVirtualAsync(filename)));
            jsonResult.Append(",\"offset\":");
            jsonResult.Append(offset);

            try
            {
                await using FileStream fs = new(filename, FileMode.Open, FileAccess.Read, FileShare.Read, Settings.FileBufferSize);
                fs.Seek(offset, SeekOrigin.Begin);

                byte[] data = new byte[Settings.FileBufferSize];
                int bytesRead = await fs.ReadAsync(data);
                if (bytesRead < 2)
                {
                    throw new ArgumentException("EOF or line too short");
                }
                int bytesProcessed = 0;

                jsonResult.Append(",\"data\":\"");
                try
                {
                    int charsWritten = 0;
                    while (charsWritten < MaxThumbnailLength)
                    {
                        // Read the next line comment
                        bool isLineStart = true;
                        int lineStart = bytesProcessed, lineLength = 0;
                        while (bytesProcessed < bytesRead && charsWritten + lineLength < MaxThumbnailLength)
                        {
                            char c = (char)data[bytesProcessed++];

                            if (isLineStart)
                            {
                                if (c == ';' || char.IsWhiteSpace(c))
                                {
                                    lineStart++;
                                    continue;
                                }
                                else
                                {
                                    isLineStart = false;
                                }
                            }

                            if (c == '\r' || c == '\n')
                            {
                                break;
                            }
                            lineLength++;
                        }

                        // Is it the end of this thumbnail?
                        string content = Encoding.ASCII.GetString(data, lineStart, lineLength);
                        if ((charsWritten + lineLength < MaxThumbnailLength && lineLength == 0) ||
                            content.StartsWith("thumbnail end") ||
                            content.StartsWith("thumbnail_JPG end") ||
                            content.StartsWith("thumbnail_QOI end"))
                        {
                            offset = 0;
                            break;
                        }

                        // Copy the data
                        jsonResult.Append(content);
                        charsWritten += lineLength;
                    }
                    offset += bytesProcessed;
                }
                finally
                {
                    jsonResult.Append("\",\"next\":");
                    jsonResult.Append(offset);
                }
                jsonResult.AppendLine(",\"err\":0}}");
            }
            catch
            {
                jsonResult.AppendLine(",\"err\":1}}");
            }
            return jsonResult.ToString();
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
            await using (FileStream fileStream = new(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, Settings.FileBufferSize))
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
                        string bufferString = Encoding.UTF8.GetString(buffer[..bytesRead].Span);
                        int simulationMarkerPosition = bufferString.IndexOf(SimulatedTimeString, StringComparison.InvariantCultureIgnoreCase);
                        if (simulationMarkerPosition >= 0)
                        {
                            offset = bytesRead - simulationMarkerPosition;
                            truncate = true;
                        }
                    }
                    fileStream.Seek(-offset, SeekOrigin.End);
                }

                // Write the simulated time
                await using (StreamWriter writer = new(fileStream, Encoding.UTF8, Settings.FileBufferSize, true))
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
    }
}
