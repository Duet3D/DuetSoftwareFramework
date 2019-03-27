using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer
{
    public static partial class FileHelper
    {
        public static async Task<ParsedFileInfo> GetFileInfo(string fileName)
        {
            FileStream fileStream = new FileStream(fileName, FileMode.Open);
            StreamReader reader = new StreamReader(fileStream);
            try
            {
                ParsedFileInfo result = new ParsedFileInfo
                {
                    FileName = fileName,
                    Size = fileStream.Length,
                    LastModified = File.GetLastWriteTime(fileName)
                };

                if (fileStream.Length > 0)
                {
                    await ParseHeader(reader, result);
                    await ParseFooter(reader, result);
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

        private static async Task ParseHeader(StreamReader reader, ParsedFileInfo partialFileInfo)
        {
            // Every time CTS.Token is accessed a copy is generated. Hence we cache one until this method completes
            CancellationToken token = Program.CancelSource.Token;

            List<double> filamentConsumption = new List<double>();
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
                        CodeParameter param = code.Parameters.Find(item => item.Letter == 'Z');
                        if (param != null)
                        {
                            float z = param.AsFloat;
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
                    gotNewInfo |= FindFilamentUsed(line, ref filamentConsumption);
                    gotNewInfo |= partialFileInfo.GeneratedBy == null && FindGeneratedBy(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.PrintTime == 0 && FindPrintTime(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.SimulatedTime == 0 && FindSimulatedTime(line, ref partialFileInfo);
                }

                if (!gotNewInfo && !lastLineHadInfo && IsFileInfoComplete(partialFileInfo))
                {
                    break;
                }
                lastLineHadInfo = gotNewInfo;
            }
            while (reader.BaseStream.Position < Settings.FileInfoReadLimit);

            partialFileInfo.Filament = filamentConsumption.ToArray();
        }

        private static async Task ParseFooter(StreamReader reader, ParsedFileInfo partialFileInfo)
        {
            CancellationToken token = Program.CancelSource.Token;
            reader.BaseStream.Seek(0, SeekOrigin.End);

            bool inRelativeMode = false, lastLineHadInfo = false;
            double? lastZ = null;
            List<double> filamentConsumption = new List<double>(partialFileInfo.Filament);

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
                        CodeParameter param = code.Parameters.Find(item => item.Letter == 'Z');
                        if (param != null && (code.Comment == null || !code.Comment.TrimStart().StartsWith("E")))
                        {
                            gotNewInfo = true;
                            if (lastZ == null)
                            {
                                lastZ = param.AsFloat;
                            }
                            else
                            {
                                double z = param.AsFloat;
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
                    gotNewInfo |= FindFilamentUsed(line, ref filamentConsumption);
                    // gotNewInfo |= partialFileInfo.GeneratedBy == null) && FindSlicer(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.PrintTime == 0 && FindPrintTime(line, ref partialFileInfo);
                    gotNewInfo |= partialFileInfo.SimulatedTime == 0 && FindSimulatedTime(line, ref partialFileInfo);
                }

                if (!gotNewInfo && !lastLineHadInfo && IsFileInfoComplete(partialFileInfo))
                {
                    break;
                }
                lastLineHadInfo = gotNewInfo;
            }
            while (reader.BaseStream.Length - reader.BaseStream.Position < Settings.FileInfoReadLimit);

            partialFileInfo.Filament = filamentConsumption.ToArray();
            if (lastZ != null && partialFileInfo.Height == 0)
            {
                partialFileInfo.Height = lastZ.Value;
            }
        }

        private static async Task<string> ReadLineFromEndAsync(StreamReader reader)
        {
            const int bufferSize = 512;
            char[] buffer = new char[bufferSize];

            string line = "";
            long startPosition = reader.BaseStream.Position, totalBytesRead = 0;
            while (reader.BaseStream.Position > 0)
            {
                // Read a chunk. Do not do this char-wise for performance reasons
                reader.BaseStream.Seek(-Math.Min(reader.BaseStream.Position, bufferSize), SeekOrigin.Current);
                reader.DiscardBufferedData();
                int bytesRead = await reader.ReadBlockAsync(buffer);

                // Keep reading until a NL is found
                for (int i = bytesRead - 1; i >= 0; i--)
                {
                    char c = buffer[i];
                    if (c == '\n')
                    {
                        reader.BaseStream.Position = startPosition - totalBytesRead - 1;
                        return line;
                    }
                    if (c != '\r')
                    {
                        line = c + line;
                    }
                    totalBytesRead++;
                }
            }
            return null;
        }

        private static bool IsFileInfoComplete(ParsedFileInfo result)
        {
            return (result.Height != 0) &&
                    (result.FirstLayerHeight != 0) &&
                    (result.LayerHeight != 0) &&
                    (result.Filament.Length > 0) &&
                    (result.GeneratedBy != null);
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
                            fileInfo.LayerHeight = double.Parse(grp.Value);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool FindFilamentUsed(string line, ref List<double> filaments)
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
                                filaments.Add(double.Parse(c.Value));
                            }
                            hadMatch = true;
                        }
                        else if (grp.Name == "m")
                        {
                            foreach (Capture c in grp.Captures)
                            {
                                filaments.Add(double.Parse(c.Value) * 1000);
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
                    double time = 0;
                    foreach (Group grp in match.Groups)
                    {
                        if (!string.IsNullOrEmpty(grp.Value))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    time += double.Parse(grp.Value) * 3600;
                                    break;
                                case "m":
                                    time += double.Parse(grp.Value) * 60;
                                    break;
                                case "s":
                                    time += double.Parse(grp.Value);
                                    break;
                            }
                        }
                    }
                    fileInfo.PrintTime = time;
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
                    double time = 0;
                    foreach (Group grp in match.Groups)
                    {
                        if (!string.IsNullOrEmpty(grp.Value))
                        {
                            switch (grp.Name)
                            {
                                case "h":
                                    time += double.Parse(grp.Value) * 3600;
                                    break;
                                case "m":
                                    time += double.Parse(grp.Value) * 60;
                                    break;
                                case "s":
                                    time += double.Parse(grp.Value);
                                    break;
                            }
                        }
                    }
                    fileInfo.SimulatedTime = time;
                    return true;
                }
            }
            return false;
        }
    }
}
