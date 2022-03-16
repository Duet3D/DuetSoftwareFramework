using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace DuetControlServer.Files.ImageProcessing
{
    /// <summary>
    /// Functions for special thumbnail parsing
    /// </summary>
    public static class IconImageParser
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Try to extract thumbnails from a given file
        /// </summary>
        /// <param name="reader">Stream reader to read from</param>
        /// <param name="codeParserBuffer">Read buffer</param>
        /// <param name="parsedFileInfo">File information</param>
        /// <param name="code">Code instance to reuse</param>
        /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
        /// <returns>Asynchronous task</returns>
        public static async ValueTask ProcessAsync(StreamReader reader, CodeParserBuffer codeParserBuffer, GCodeFileInfo parsedFileInfo, Code code, bool readThumbnailContent)
        {
            _logger.Info($"Processing Image {parsedFileInfo.FileName}");
            bool offsetAdjusted = false;
            long offset = codeParserBuffer.GetPosition(reader);
            code.Reset();

            // Keep reading the data from the file
            StringBuilder imageBuffer = new();
            while (codeParserBuffer.GetPosition(reader) < reader.BaseStream.Length)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();
                if (!await Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }

                // Icon data goes until the first line of executable code.
                if (code.Type == CodeType.Comment)
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
                        _logger.Error("Icon Thumbnails Found");
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

            using MemoryStream ms = new(Convert.FromBase64String(finalString));
            using MemoryStream bitmapSource = new();
            _logger.Debug("Encoding Image");
            try
            {
                using Image image = BinaryToImage(ms, out int width, out int height);
                string data = null;
                if (readThumbnailContent)
                {
                    using MemoryStream memoryStream = new();
                    image.Save(memoryStream, PngFormat.Instance);
                    memoryStream.TryGetBuffer(out ArraySegment<byte> buffer);
                    data = Convert.ToBase64String(buffer.Array, 0, (int)memoryStream.Length);
                }
                _logger.Debug(data);

                return new()
                {
                    Data = data,
                    Format = ThumbnailInfoFormat.PNG,
                    Height = height,
                    Width = width,
                    Size = finalString.Length
                };
            }
            catch (Exception ex)
            {
                var imageProcessingException = new ImageProcessingException("Error processing Icon image", ex);
                _logger.Error(imageProcessingException);
                throw imageProcessingException;
            }
        }

        /// <summary>
        /// Takes a memory stream containing the header icon + the 4 size bytes
        /// </summary>
        /// <param name="ms">memory stream containing the header icon + 4 size-bytes</param>
        /// <param name="width">Width of the thumbnail</param>
        /// <param name="height">Height of the thumbnail</param>
        /// <returns>Parsed image</returns>
        public static Image BinaryToImage(MemoryStream ms, out int width, out int height)
        {
            ms.Position = 0;
            width = ms.ReadByte() << 8 | ms.ReadByte();
            height = ms.ReadByte() << 8 | ms.ReadByte();

            var target = new Image<Rgba32>(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> pixelRowSpan = target.GetPixelRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte upper = (byte)ms.ReadByte();
                    byte lower = (byte)ms.ReadByte();
                    int color = lower | upper << 8;
                    byte[] rgb = RGB2To3Bytes(color);
                    pixelRowSpan[x] = new Rgba32(rgb[0], rgb[1], rgb[2]);
                }
            }
            return target;
        }

        /// <summary>
        /// Convert two bytes compressed RGB to three bytes
        /// </summary>
        /// <param name="color"></param>
        /// <returns>[0]: Red, [1]: Green, [2]: Blue </returns>
        public static byte[] RGB2To3Bytes(int color)
        {
            byte[] result = new byte[3];

            result[0] = (byte)((color & 0xF800) >> 8);  // Red
            result[1] = (byte)((color & 0x07E0) >> 3);  // Green
            result[2] = (byte)((color & 0x001F) << 3);  // Blue

            return result;
        }
    }
}
