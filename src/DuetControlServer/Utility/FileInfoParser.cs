using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer
{
    /// <summary>
    /// Static class used to retrieve information from G-code jobs
    /// </summary>
    public static class FileInfoParser
    {
        /// <summary>
        /// Parse a G-code file
        /// </summary>
        /// <param name="fileName">File to analyze</param>
        /// <returns>Information about the file</returns>
        public static async Task<ParsedFileInfo> Parse(string fileName)
        {
            using FileStream fileStream = new FileStream(fileName, FileMode.Open);
            using StreamReader reader = new StreamReader(fileStream);
            ParsedFileInfo result = new ParsedFileInfo
            {
                FileName = await FilePath.ToVirtualAsync(fileName),
                Size = fileStream.Length,
                LastModified = File.GetLastWriteTime(fileName)
            };

            if (fileStream.Length > 0)
            {
                List<float> filamentConsumption = new List<float>();
                await ParseHeader(reader, filamentConsumption, result);
                await ParseFooter(reader, filamentConsumption, result);
                result.Filament = filamentConsumption;

                if (result.FirstLayerHeight + result.LayerHeight > 0F && result.Height > 0F)
                {
                    result.NumLayers = (int?)(Math.Round((result.Height - result.FirstLayerHeight) / result.LayerHeight) + 1);
                }
            }
            return result;
        }

        private static async Task ParseHeader(StreamReader reader, List<float> filament, ParsedFileInfo partialFileInfo)
        {
            // Every time CTS.Token is accessed a copy is generated. Hence we cache one until this method completes
            CancellationToken token = Program.CancelSource.Token;

            long bytesRead = 0;
            bool inRelativeMode = false, lastLineHadInfo = false;
            do
            {
                token.ThrowIfCancellationRequested();
                string line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }
                bytesRead += reader.CurrentEncoding.GetByteCount(line) + 1;     // This may be off by one byte in case '\r\n' is used
                bool gotNewInfo = false;

                // See what code to deal with
                Code code = new Code(line);
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
                else if (code.Type == CodeType.Comment)
                {
                    gotNewInfo |= partialFileInfo.LayerHeight == 0 && FindLayerHeight(line, ref partialFileInfo);
                    gotNewInfo |= FindFilamentUsed(line, ref filament);
                    gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.PrintTime == 0 && FindPrintTime(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.SimulatedTime == 0 && FindSimulatedTime(line, ref partialFileInfo);
                }

                if (!gotNewInfo && !lastLineHadInfo && IsFileInfoComplete(partialFileInfo, filament))
                {
                    break;
                }
                lastLineHadInfo = gotNewInfo;
            }
            while (bytesRead < Settings.FileInfoReadLimitHeader);
        }

        private static async Task ParseFooter(StreamReader reader, List<float> filament, ParsedFileInfo partialFileInfo)
        {
            CancellationToken token = Program.CancelSource.Token;
            reader.BaseStream.Seek(0, SeekOrigin.End);

            bool inRelativeMode = false, lastLineHadInfo = false, hadFilament = filament.Count > 0;

            char[] buffer = new char[512];
            int bufferPointer = 0;
            long bytesRead = 0;
            do
            {
                token.ThrowIfCancellationRequested();

                // Read another line
                ReadLineFromEndResult readResult = await ReadLineFromEndAsync(reader, buffer, bufferPointer);
                if (readResult == null)
                {
                    break;
                }
                bufferPointer = readResult.BufferPointer;
                bytesRead += reader.CurrentEncoding.GetByteCount(readResult.Line) + 1;     // This may be off by one byte in case '\r\n' is used
                bool gotNewInfo = false;

                // See what code to deal with
                Code code = new Code(readResult.Line);
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
                else if (code.Type == CodeType.Comment)
                {
                    gotNewInfo |= partialFileInfo.LayerHeight == 0 && FindLayerHeight(readResult.Line, ref partialFileInfo);
                    gotNewInfo |= !hadFilament && FindFilamentUsed(readResult.Line, ref filament);
                    gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(readResult.Line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.PrintTime == 0 && FindPrintTime(readResult.Line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.SimulatedTime == 0 && FindSimulatedTime(readResult.Line, ref partialFileInfo);
                }

                if (!gotNewInfo && !lastLineHadInfo && IsFileInfoComplete(partialFileInfo, filament))
                {
                    break;
                }
                lastLineHadInfo = gotNewInfo;
            }
            while (bytesRead < Settings.FileInfoReadLimitFooter);
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
            string line = "";
            do
            {
                if (bufferPointer == 0)
                {
                    if (reader.BaseStream.Position == 0)
                    {
                        return null;
                    }

                    int bytesToRewind = (int)Math.Min(reader.BaseStream.Position, buffer.Length);
                    long newPosition = reader.BaseStream.Position - bytesToRewind;
                    reader.BaseStream.Seek(newPosition, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
                    bufferPointer = await reader.ReadBlockAsync(buffer);
                    reader.BaseStream.Seek(newPosition, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
                }

                char c = buffer[--bufferPointer];
                if (c == '\n')
                {
                    return new ReadLineFromEndResult
                    {
                        Line = line,
                        BufferPointer = bufferPointer
                    };
                }
                if (c != '\r')
                {
                    line = c + line;
                }
            }
            while (true);
        }

        private static bool IsFileInfoComplete(ParsedFileInfo result, List<float> filament)
        {
            return (result.Height != 0) &&
                    (result.FirstLayerHeight != 0) &&
                    (result.LayerHeight != 0) &&
                    (filament.Count > 0) &&
                    (!string.IsNullOrEmpty(result.GeneratedBy));
        }

        private static bool FindLayerHeight(string line, ref ParsedFileInfo fileInfo)
        {
            foreach (Regex item in Settings.LayerHeightFilters)
            {
                Match match = item.Match(line);
                if (match.Success)
                {
                    foreach (Group grp in match.Groups)
                    {
                        if (grp.Name == "mm")
                        {
                            fileInfo.LayerHeight = float.Parse(grp.Value);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool FindFilamentUsed(string line, ref List<float> filaments)
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
                                filaments.Add(float.Parse(c.Value));
                            }
                            hadMatch = true;
                        }
                        else if (grp.Name == "m")
                        {
                            foreach (Capture c in grp.Captures)
                            {
                                filaments.Add(float.Parse(c.Value) * 1000);
                            }
                            hadMatch = true;
                        }
                    }
                }
            }
            return hadMatch;
        }

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
                        if (!string.IsNullOrEmpty(grp.Value))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    seconds += long.Parse(grp.Value) * 3600;
                                    break;
                                case "m":
                                    seconds += long.Parse(grp.Value) * 60;
                                    break;
                                case "s":
                                    seconds += long.Parse(grp.Value);
                                    break;
                            }
                        }
                    }
                    fileInfo.PrintTime = seconds;
                    return true;
                }
            }
            return false;
        }

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
                        if (!string.IsNullOrEmpty(grp.Value))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    seconds += long.Parse(grp.Value) * 3600;
                                    break;
                                case "m":
                                    seconds += long.Parse(grp.Value) * 60;
                                    break;
                                case "s":
                                    seconds += long.Parse(grp.Value);
                                    break;
                            }
                        }
                    }
                    fileInfo.SimulatedTime = seconds;
                    return true;
                }
            }
            return false;
        }
    }
}
