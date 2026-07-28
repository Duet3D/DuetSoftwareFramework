using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files.ImageProcessing;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Files.Parser.ImageProcessing;

/// <summary>
/// Functions for special thumbnail parsing
/// </summary>
public static class IconImageParser
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static ILogger? _logger;

    /// <summary>
    /// Set the logger (called during initialization)
    /// </summary>
    public static void SetLogger(ILogger<FileInfoParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Try to extract thumbnails from a given file
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <param name="codeParserBuffer">Read buffer</param>
    /// <param name="parsedFileInfo">File information</param>
    /// <param name="code">Code instance to reuse</param>
    /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async ValueTask ProcessAsync(Stream stream, CodeParserBuffer codeParserBuffer, GCodeFileInfo parsedFileInfo, Code code, bool readThumbnailContent, CancellationToken cancellationToken)
    {
        _logger?.LogInformation($"Processing Image {parsedFileInfo.FileName}");
        bool offsetAdjusted = false;
        long offset = codeParserBuffer.GetPosition(stream);
        code.Reset();

        // Keep reading the data from the file
        StringBuilder imageBuffer = new();
        while (codeParserBuffer.GetPosition(stream) < stream.Length)
        {
            if (!await Code.ParseAsync(stream, code, codeParserBuffer, cancellationToken))
            {
                continue;
            }

            // Icon data goes until the first line of executable code.
            if (code.Type == CodeType.Comment && code.Comment is not null)
            {
                if (!offsetAdjusted)
                {
                    offset++;     // for leading semicolon
                    foreach (char c in code.Comment)
                    {
                        if (char.IsWhiteSpace(c))
                        {
                            offset++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    offsetAdjusted = true;
                }
                imageBuffer.Append(code.Comment.Trim());
                code.Reset();
            }
            else
            {
                try
                {
                    ThumbnailInfo thumbnail = ReadImage(imageBuffer.ToString(), readThumbnailContent);
                    thumbnail.Offset = offset;
                    parsedFileInfo.Thumbnails.Add(thumbnail);
                    _logger?.LogInformation("Icon Thumbnails Found");
                }
                catch
                {
                    //throw it away
                }
                return;
            }

        }
    }

    private static ThumbnailInfo ReadImage(string imageBuffer, bool readThumbnailContent)
    {
        // Convert the string into a usable format
        string finalString = imageBuffer
            .Replace("Icon: ", string.Empty)
            .Replace(";", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("\r\n", string.Empty);

        _logger?.LogDebug("Encoding Image");
        try
        {
            byte[] iconData = Convert.FromBase64String(finalString);
            if (iconData.Length < 4)
            {
                throw new ImageProcessingException();
            }

            // Icons start with big-endian width and height words followed by RGB565 pixel data
            int width = iconData[0] << 8 | iconData[1], height = iconData[2] << 8 | iconData[3];
            if (width == 0 || height == 0 || iconData.Length < 4 + 2L * width * height)
            {
                throw new ImageProcessingException();
            }

            return new()
            {
                Data = readThumbnailContent ? Convert.ToBase64String(PngWriter.Encode(ConvertPixelData(iconData, width, height), width, height)) : null,
                Format = ThumbnailInfoFormat.PNG,
                Height = height,
                Width = width,
                Size = finalString.Length
            };
        }
        catch (Exception e) when (e is not ImageProcessingException)
        {
            ImageProcessingException imageProcessingException = new("Error processing Icon image", e);
            _logger?.LogError(imageProcessingException, "Error processing Icon image");
            throw imageProcessingException;
        }
    }

    /// <summary>
    /// Convert the RGB565 pixel data of an icon to RGB888
    /// </summary>
    /// <param name="iconData">Icon data starting with the width and height words</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <returns>Pixel data with three bytes per pixel in RGB order</returns>
    private static byte[] ConvertPixelData(ReadOnlySpan<byte> iconData, int width, int height)
    {
        byte[] pixels = new byte[3 * width * height];
        for (int i = 0; i < width * height; i++)
        {
            int color = iconData[4 + 2 * i] << 8 | iconData[5 + 2 * i];
            pixels[3 * i] = (byte)((color & 0xF800) >> 8);
            pixels[3 * i + 1] = (byte)((color & 0x07E0) >> 3);
            pixels[3 * i + 2] = (byte)((color & 0x001F) << 3);
        }
        return pixels;
    }
}
