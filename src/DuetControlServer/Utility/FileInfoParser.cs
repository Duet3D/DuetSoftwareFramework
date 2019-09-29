using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using Zhaobang.IO;
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
            FileStream fileStream = new FileStream(fileName, FileMode.Open);
            SeekableStreamReader reader = new SeekableStreamReader(fileStream);
            try
            {
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
                    await ParseFooter(reader, fileStream.Length, filamentConsumption, result);
                    result.Filament = filamentConsumption;

                    if (result.FirstLayerHeight + result.LayerHeight > 0F && result.Height > 0F)
                    {
                        result.NumLayers = (int?)(Math.Round((result.Height - result.FirstLayerHeight) / result.LayerHeight) + 1);
                    }
                }

                reader.Close();
                fileStream.Close();
                return result;
            }
            catch
            {
                reader.Close();
                fileStream.Close();
                throw;
            }
        }

        private static async Task ParseHeader(SeekableStreamReader reader, List<float> filament, ParsedFileInfo partialFileInfo)
        {
            // Every time CTS.Token is accessed a copy is generated. Hence we cache one until this method completes
            CancellationToken token = Program.CancelSource.Token;

            bool inRelativeMode = false, lastLineHadInfo = false;
            do
            {
                token.ThrowIfCancellationRequested();

                string line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }
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
            while (reader.Position < Settings.FileInfoReadLimit);
        }

        private static async Task ParseFooter(SeekableStreamReader reader, long length, List<float> filament, ParsedFileInfo partialFileInfo)
        {
            CancellationToken token = Program.CancelSource.Token;
            reader.Seek(0, SeekOrigin.End);

            bool inRelativeMode = false, lastLineHadInfo = false, hadFilament = filament.Count > 0;
            float? lastZ = null;

            do
            {
                token.ThrowIfCancellationRequested();

                // Read another line
                string line = await ReadLineFromEndAsync(reader);
                if (line == null)
                {
                    break;
                }
                bool gotNewInfo = false;

                // See what code to deal with
                Code code = new Code(line);
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
                        // G0/G1 is a move, see if there is a Z parameter present
                        // Users tend to place their own lift Z code at the end, so attempt to read two G0/G1 Z
                        // codes and check the height differene between them
                        CodeParameter zParam = code.Parameter('Z');
                        if (zParam != null && (code.Comment == null || !code.Comment.TrimStart().StartsWith("E")))
                        {
                            gotNewInfo = true;
                            if (lastZ == null)
                            {
                                lastZ = zParam;
                            }
                            else
                            {
                                float z = zParam;
                                if (lastZ - z > Settings.MaxLayerHeight)
                                {
                                    partialFileInfo.Height = z;
                                }
                                else
                                {
                                    partialFileInfo.Height = lastZ.Value;
                                }
                                break;
                            }
                        }
                    }
                }
                else if (code.Type == CodeType.Comment)
                {
                    gotNewInfo |= partialFileInfo.LayerHeight == 0 && FindLayerHeight(line, ref partialFileInfo);
                    gotNewInfo |= !hadFilament && FindFilamentUsed(line, ref filament);
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
            while (length - reader.Position < Settings.FileInfoReadLimit);

            if (lastZ != null && partialFileInfo.Height == 0)
            {
                partialFileInfo.Height = lastZ.Value;
            }
        }

        private static async Task<string> ReadLineFromEndAsync(SeekableStreamReader reader)
        {
            const int bufferSize = 128;
            char[] buffer = new char[bufferSize];

            string line = string.Empty;
            while (reader.Position > 0)
            {
                // Read a chunk. Do not do this char-wise for performance reasons
                long bytesToRead = Math.Min(reader.Position, bufferSize);
                reader.Seek(-bytesToRead, SeekOrigin.Current);
                int bytesRead = await reader.ReadBlockAsync(buffer);
                reader.Seek(-bytesRead, SeekOrigin.Current);

                // Keep reading until a NL is found
                for (int i = (int)Math.Min(bytesRead - 1, reader.Position); i >= 0; i--)
                {
                    char c = buffer[i];
                    if (c == '\n')
                    {
                        reader.Seek(i, SeekOrigin.Current);
                        return line;
                    }
                    if (c != '\r')
                    {
                        line = c + line;
                    }
                }
            }
            return null;
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
