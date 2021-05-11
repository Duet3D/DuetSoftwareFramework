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
    public static class IconImageParser
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();



        public static async Task ProcessAsync(StreamReader reader, CodeParserBuffer codeParserBuffer, ParsedFileInfo parsedFileInfo, Code code)
        {
            _logger.Info($"Processing Image {parsedFileInfo.FileName}");
            StringBuilder imageBuffer = new();
            code.Reset();

            //Keep reading the data from the file
            while (codeParserBuffer.GetPosition(reader) < reader.BaseStream.Length)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();
                if (!await DuetAPI.Commands.Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }

                //Icon data goes until the first line of executable code.
                if (code.Type == CodeType.Comment)
                {
                    imageBuffer.Append(code.Comment.Trim());
                    code.Reset();
                }
                else
                {
                    try
                    {
                        ParsedThumbnail thumbnail = ReadImage(imageBuffer.ToString());
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

        private static ParsedThumbnail ReadImage(string imageBuffer)
        {
            ParsedThumbnail thumbnail = new();
            //Convert the string into a usable format
            var finalString = imageBuffer.Replace("Icon: ", String.Empty).Replace(";", string.Empty).Replace(" ", string.Empty).Replace("\r\n", string.Empty);

            using MemoryStream ms = new(Convert.FromBase64String(finalString));
            using MemoryStream bitmapSource = new();
            _logger.Debug("Encoding Image");
            try
            {
                var image = BinaryToImage(ms, out int width, out int height);
                thumbnail.EncodedImage = image.ToBase64String(PngFormat.Instance);
                _logger.Debug(thumbnail.EncodedImage);
                thumbnail.Width = width;
                thumbnail.Height = height;
                image?.Dispose(); //Clean up image after getting data from it.
                return thumbnail;
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
        /// <returns></returns>
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
