using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files.ImageProcessing;
using DuetControlServer.Files.Parser.ImageProcessing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files.Parser;

/// <summary>
/// Class used to retrieve information from G-code jobs
/// </summary>
/// <param name="codeFactory">Code factory</param>
/// <param name="expressions">Expression evaluator</param>
/// <param name="filePath">File path helper</param>
/// <param name="settings">Settings</param>
public class FileInfoParser(CodeFactory codeFactory, Expressions expressions, FilePathResolver filePath, ILogger<FileInfoParser> logger, IOptions<Settings> settings)
{
    /// <summary>
    /// Parse a G-code file
    /// </summary>
    /// <param name="fileName">File to analyze</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Information about the file</returns>
    public async Task<GCodeFileInfo> ParseAsync(string fileName, bool readThumbnailContent, CancellationToken cancellationToken = default)
    {
        await using FileStream fileStream = new(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
        GCodeFileInfo result = new()
        {
            FileName = await filePath.ToVirtualAsync(fileName, cancellationToken: cancellationToken),
            LastModified = File.GetLastWriteTime(fileName),
            Size = fileStream.Length
        };

        // Only allow job and macro files to be parsed
        bool isValidFileToParse = fileStream.Length > 0 && (
            fileName.EndsWith(".gcode", StringComparison.InvariantCultureIgnoreCase) ||
            fileName.EndsWith(".g", StringComparison.InvariantCultureIgnoreCase) ||
            fileName.EndsWith(".gco", StringComparison.InvariantCultureIgnoreCase) ||
            fileName.EndsWith(".gc", StringComparison.InvariantCultureIgnoreCase) ||
            fileName.EndsWith(".nc", StringComparison.InvariantCultureIgnoreCase)
        );
        if (!isValidFileToParse)
        {
            string macroDirectory = await filePath.ToPhysicalAsync(string.Empty, FileDirectory.Macros, cancellationToken);
            isValidFileToParse = fileName.StartsWith(macroDirectory);
        }

        if (isValidFileToParse)
        {
            Dictionary<string, Task<object?>> evaluationTasks = [];

            // Parse the file
            await ParseHeaderAsync(fileStream, readThumbnailContent, evaluationTasks, result, cancellationToken);
            await ParseFooterAsync(fileStream, result, cancellationToken);

            // Wait for key-value evaluation tasks to finish and add the results
            foreach (KeyValuePair<string, Task<object?>> kvp in evaluationTasks)
            {
                try
                {
                    object? value = await kvp.Value;
                    result.CustomInfo.Add(kvp.Key, JsonSerializer.SerializeToElement(value));
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to evaluate key '{Key}' from job file '{File}'", kvp.Key, result.FileName);
                }
            }

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
    /// <param name="stream">Stream</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <param name="userDefinedKeys">User-defined keys and the corresponding evaluation task</param>
    /// <param name="partialFileInfo">G-code file information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task ParseHeaderAsync(Stream stream, bool readThumbnailContent, Dictionary<string, Task<object?>> userDefinedKeys, GCodeFileInfo partialFileInfo, CancellationToken cancellationToken)
    {
        Code code = codeFactory.Create();
        CodeParserBuffer codeParserBuffer = new(settings.Value.FileBufferSize, true);

        bool lastCodeHadInfo = false, gotNewInfo = false;
        long fileReadLimit = Math.Min(settings.Value.FileInfoReadLimitHeader, stream.Length);
        while (codeParserBuffer.GetPosition(stream) < fileReadLimit || gotNewInfo)
        {
            gotNewInfo = false;
            if (!await DuetAPI.Commands.Code.ParseAsync(stream, code, codeParserBuffer, cancellationToken))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(code.Comment))
            {
                gotNewInfo |= (partialFileInfo.SimulatedTime is null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                gotNewInfo |= !gotNewInfo && (partialFileInfo.PrintTime is null) && FindPrintTime(code.Comment, ref partialFileInfo);
                gotNewInfo |= (partialFileInfo.LayerHeight == 0) && FindLayerHeight(code.Comment, ref partialFileInfo);
                gotNewInfo |= (partialFileInfo.NumLayers == 0) && FindNumLayers(code.Comment, ref partialFileInfo);
                gotNewInfo |= FindFilamentUsed(code.Comment, ref partialFileInfo);
                gotNewInfo |= AddUserDefinedKey(code, userDefinedKeys);
                gotNewInfo |= string.IsNullOrEmpty(partialFileInfo.GeneratedBy) && FindGeneratedBy(code.Comment, ref partialFileInfo);
                gotNewInfo |= await ParseThumbnails(stream, code, codeParserBuffer, partialFileInfo, readThumbnailContent, cancellationToken);
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
    /// <param name="stream">Stream</param>
    /// <param name="partialFileInfo">G-code file information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task ParseFooterAsync(Stream stream, GCodeFileInfo partialFileInfo, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.End);
        ReadLineFromEndData readData = new(stream.Position, settings.Value.FileBufferSize);
        byte[] buffer = new byte[settings.Value.FileBufferSize];

        Code code = codeFactory.Create();
        bool inRelativeMode = false, lastLineHadInfo = false, hadFilament = partialFileInfo.Filament.Count > 0;
        do
        {
            // Read another line
            if (!await ReadLineFromEndAsync(stream, buffer, readData, cancellationToken))
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
                        else if ((code.MajorNumber == 0 || code.MajorNumber == 1) && code.TryGetFloat('Z', out float zParam) &&
                                    (code.Comment is null || !code.Comment.TrimStart().StartsWith("E", StringComparison.InvariantCultureIgnoreCase)))
                        {
                            // G0/G1 is an absolute move
                            gotNewInfo = true;
                            partialFileInfo.Height = zParam;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(code.Comment))
                    {
                        gotNewInfo |= (partialFileInfo.SimulatedTime is null) && FindSimulatedTime(code.Comment, ref partialFileInfo);
                        gotNewInfo |= !gotNewInfo && (partialFileInfo.PrintTime is null) && FindPrintTime(code.Comment, ref partialFileInfo);
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
        while (stream.Length - stream.Position < settings.Value.FileInfoReadLimitFooter + buffer.Length);
    }

    /// <summary>
    /// Result for wrapping the buffer pointer because ref parameters are not supported for async functions
    /// </summary>
    private class ReadLineFromEndData
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="filePosition">File position</param>
        /// <param name="fileBufferSize">File buffer size</param>
        public ReadLineFromEndData(long filePosition, int fileBufferSize)
        {
            FilePosition = filePosition;
            LineBuffer = new byte[fileBufferSize];
        }

        /// <summary>
        /// Read line
        /// </summary>
        public string Line = string.Empty;

        /// <summary>
        /// New pointer in the buffer
        /// </summary>
        public int BufferPointer;

        /// <summary>
        /// Last file position
        /// </summary>
        public long FilePosition;

        /// <summary>
        /// Buffer used for caching line data
        /// </summary>
        public byte[] LineBuffer;
    }

    /// <summary>
    /// Read another line from the end of a file
    /// </summary>
    /// <param name="stream">Stream</param>
    /// <param name="buffer">Internal buffer</param>
    /// <param name="readData">Data about the read progress while reading backwards</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether another line could be read</returns>
    private static async ValueTask<bool> ReadLineFromEndAsync(Stream stream, byte[] buffer, ReadLineFromEndData readData, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        for(;;)
        {
            // Read more from the file if necessary
            if (readData.BufferPointer == 0 && readData.FilePosition != 0)
            {
                if (readData.FilePosition < buffer.Length)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    readData.BufferPointer = Math.Min(await stream.ReadAsync(buffer, cancellationToken), (int)readData.FilePosition);
                    readData.FilePosition = 0;
                }
                else
                {
                    readData.FilePosition -= Math.Min(readData.FilePosition, buffer.Length);
                    stream.Seek(readData.FilePosition, SeekOrigin.Begin);
                    readData.BufferPointer = await stream.ReadAsync(buffer, cancellationToken);
                }
            }

            // Stop reading if we've reached NL or SOF
            byte c = (readData.BufferPointer == 0) ? (byte)0 : buffer[--readData.BufferPointer];
            if (c == '\0' || c == '\n')
            {
                if (c == '\0' && bytesRead == 0)
                {
                    // reached SOF, cannot read any more
                    return false;
                }
                if (bytesRead == readData.LineBuffer.Length)
                {
                    readData.Line = string.Empty;   // overflow
                    return true;
                }

                void SetLine()
                {
                    Span<byte> lineBuffer = readData.LineBuffer.AsSpan(readData.LineBuffer.Length - bytesRead);
                    if (lineBuffer.Length >= 3 && lineBuffer[0] == 0xEF && lineBuffer[1] == 0xBB && lineBuffer[2] == 0xBF)
                    {
                        // Skip BOM in UTF-8 files
                        lineBuffer = lineBuffer[3..];
                    }
                    readData.Line = Encoding.UTF8.GetString(lineBuffer);
                }
                SetLine();
                return true;
            }

            // Add more to the line buffer if possible
            if (c != '\r' && bytesRead < readData.LineBuffer.Length)
            {
                bytesRead++;
                readData.LineBuffer[^bytesRead] = c;
            }
        }
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
    private bool FindLayerHeight(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.LayerHeightFilters)
        {
            Match match = item.Match(line);
            if (match.Success)
            {
                foreach (Group grp in match.Groups.Cast<Group>())
                {
                    if (grp.Name == "mm" && float.TryParse(grp.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float layerHeight) &&
                        float.IsFinite(layerHeight) && layerHeight < settings.Value.MaxLayerHeight)
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
    private bool FindNumLayers(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.NumLayersFilters)
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
    private bool FindFilamentUsed(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.FilamentFilters)
        {
            Match match = item.Match(line);
            if (match.Success)
            {
                if (match.Groups.TryGetValue("mm", out Group? mmGroup))
                {
                    if (float.TryParse(mmGroup.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                        float.IsFinite(filamentUsage))
                    {
                        if (match.Groups.TryGetValue("index", out Group? indexGroup) && int.TryParse(indexGroup.Value, out int index))
                        {
                            for (int i = fileInfo.Filament.Count; i <= index; i++)
                            {
                                fileInfo.Filament.Add(0F);
                            }
                            fileInfo.Filament[index] = filamentUsage;
                        }
                        else
                        {
                            foreach (Capture capture in mmGroup.Captures.Cast<Capture>())
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

                if (match.Groups.TryGetValue("m", out Group? mGroup))
                {
                    if (float.TryParse(mGroup.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float filamentUsage) &&
                        float.IsFinite(filamentUsage))
                    {
                        if (match.Groups.TryGetValue("index", out Group? indexGroup) && int.TryParse(indexGroup.Value, out int index))
                        {
                            for (int i = fileInfo.Filament.Count; i <= index; i++)
                            {
                                fileInfo.Filament.Add(0F);
                            }
                            fileInfo.Filament[index] = filamentUsage * 1000F;
                        }
                        else
                        {
                            foreach (Capture capture in mGroup.Captures.Cast<Capture>())
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

    private const string CustomInfoPrefix = "customInfo";

    /// <summary>
    /// Check if this line contains a user-defined key and add it if that is the case
    /// </summary>
    /// <param name="code">Code possibly containing the user-defined key-value pair</param>
    /// <param name="userDefinedKeys">Dictionary of user-defined key vs. value evaluation task</param>
    private bool AddUserDefinedKey(Code code, Dictionary<string, Task<object?>> userDefinedKeys)
    {
        if (code.Comment!.StartsWith(CustomInfoPrefix))
        {
            string comment = code.Comment[CustomInfoPrefix.Length..];
            int index = comment.IndexOf('=');
            if (index > 0)
            {
                string key = comment[..index].Trim(), value = comment[(index + 1)..].Trim();
                logger.LogDebug("Evaluating user-defined key '{Key}' with value '{Value}'", key, value);
                userDefinedKeys.Add(key, expressions.EvaluateExpressionRaw(code, value, false));
                return true;
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
    private bool FindGeneratedBy(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.GeneratedByFilters)
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
    private bool FindPrintTime(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.PrintTimeFilters)
        {
            Match match = item.Match(line);
            if (match.Success)
            {
                long seconds = 0;
                foreach (Group grp in match.Groups.Cast<Group>())
                {
                    if (float.TryParse(grp.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float printTime) &&
                        float.IsFinite(printTime))
                    {
                        switch (grp.Name)
                        {
                            case "d":
                                seconds += (long)Math.Round(printTime) * 86400L;
                                break;
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
    private bool FindSimulatedTime(string line, ref GCodeFileInfo fileInfo)
    {
        foreach (Regex item in settings.Value.SimulatedTimeFilters)
        {
            Match match = item.Match(line);
            if (match.Success)
            {
                long seconds = 0;
                foreach (Group grp in match.Groups.Cast<Group>())
                {
                    if (long.TryParse(grp.Value, out long simulatedTime))
                    {
                        switch (grp.Name)
                        {
                            case "d":
                                seconds += simulatedTime * 86400;
                                break;
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
    /// <param name="stream">Stream</param>
    /// <param name="parsedFileInfo">G-code file information</param>
    /// <param name="codeParserBuffer">Parser buffer</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the code contains thumbnail data</returns>
    private async ValueTask<bool> ParseThumbnails(Stream stream, Code code, CodeParserBuffer codeParserBuffer, GCodeFileInfo parsedFileInfo, bool readThumbnailContent, CancellationToken cancellationToken = default)
    {
        if (code.Comment is null)
        {
            // Need a comment to start parsing thumbnails...
            return false;
        }

        // This is the start of an embedded thumbnail image
        string trimmedComment = code.Comment.TrimStart();
        if (trimmedComment.StartsWith("thumbnail begin", StringComparison.InvariantCultureIgnoreCase))
        {
            logger.LogDebug("Found embedded thumbnail PNG image");
            await ImageParser.ProcessAsync(stream, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.PNG, cancellationToken);
            return true;
        }
        if (trimmedComment.StartsWith("thumbnail_JPG", StringComparison.InvariantCultureIgnoreCase))
        {
            logger.LogDebug("Found embedded thumbnail JPG Image");
            await ImageParser.ProcessAsync(stream, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.JPEG, cancellationToken);
            return true;
        }
        if (trimmedComment.StartsWith("thumbnail_QOI", StringComparison.InvariantCultureIgnoreCase))
        {
            logger.LogDebug("Found embedded thumbnail QOI Image");
            await ImageParser.ProcessAsync(stream, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, ThumbnailInfoFormat.QOI, cancellationToken);
            return true;
        }

        // Icon Image (proprietary)
        if (trimmedComment.Contains("Icon:"))
        {
            logger.LogDebug("Found Icon Image");
            await IconImageParser.ProcessAsync(stream, codeParserBuffer, parsedFileInfo, code, readThumbnailContent, cancellationToken);
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
    private const int MaxThumbnailLength = 2600;

    /// <summary>
    /// Maximum length of file fragment data in a fragment response
    /// </summary>
    /// <remarks>
    /// See RepRapFirmware -> RepRap.cpp -> GetFileFragmentResponse
    /// </remarks>
    private const int MaxFileFragmentLength = 1024;

    /// <summary>
    /// Retrieve a chunk of a thumbnail or a file fragment
    /// </summary>
    /// <param name="filename">G-code file to parse</param>
    /// <param name="offset">File offset to start from</param>
    /// <param name="isThumbnail">Whether this is a thumbnail request</param>
    /// <param name="explicitLineNumber">Explicit line number if present</param
    /// <returns>JSON response</returns>
    public async ValueTask<string> ParseFileFragment(string filename, long offset, bool isThumbnail, long? explicitLineNumber = null)
    {
        StringBuilder jsonResult = new();
        jsonResult.Append('{');
        if (explicitLineNumber != null)
        {
            jsonResult.Append($"\"line\":{explicitLineNumber.Value},");
        }
        jsonResult.Append($"\"{(isThumbnail ? "thumbnail" : "fragment")}\":{{\"fileName\":");
        jsonResult.Append(JsonSerializer.Serialize(await filePath.ToVirtualAsync(filename)));
        jsonResult.Append(",\"offset\":");
        jsonResult.Append(offset);

        try
        {
            await using FileStream fs = new(filename, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
            fs.Seek(offset, SeekOrigin.Begin);

            byte[] data = new byte[settings.Value.FileBufferSize];
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
                if (isThumbnail)
                {
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
                else
                {
                    while (bytesProcessed < bytesRead && charsWritten + 1 < MaxFileFragmentLength)
                    {
                        // Read the next char and append it
                        char c = (char)data[bytesProcessed++];
                        if (c == '\n')
                        {
                            jsonResult.Append("\\n");
                            charsWritten += 2;
                        }
                        else if (c != '\r')
                        {
                            jsonResult.Append(c);
                            charsWritten++;
                        }
                    }
                    offset += bytesProcessed;

                    // Report EOF if we reached the end of the file
                    if (bytesProcessed == bytesRead && fs.Position == fs.Length)
                    {
                        offset = 0;
                    }
                }
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task UpdateSimulatedTimeAsync(string filename, int totalSeconds, CancellationToken cancellationToken = default)
    {
        // Get the last modified datetime
        DateTime lastWriteTime = File.GetLastWriteTime(filename);

        // Update the simulated time in the file
        await using (FileStream fileStream = new(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, settings.Value.FileBufferSize))
        {
            // Check if we need to truncate the file before the last simulated time
            bool truncate = false;
            Memory<byte> buffer = new byte[64];
            if (fileStream.Length >= buffer.Length)
            {
                fileStream.Seek(-buffer.Length, SeekOrigin.End);
                int bytesRead = await fileStream.ReadAsync(buffer, cancellationToken), offset = 0;
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
            await using (StreamWriter writer = new(fileStream, Encoding.UTF8, settings.Value.FileBufferSize, true))
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
