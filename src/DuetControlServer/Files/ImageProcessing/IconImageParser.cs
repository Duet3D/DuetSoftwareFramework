using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files.ImageProcessing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Files.ImageProcessing
{
    public static class IconImageParser
    {
        public static async Task ProcessAsync(StreamReader reader, CodeParserBuffer codeParserBuffer, ParsedFileInfo parsedFileInfo, Code code)
        {
            StringBuilder imageBuffer = new StringBuilder();
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
                if(code.Type == CodeType.Comment)
                {
                    imageBuffer.Append(code.Comment.Trim());
                    code.Reset();
                }
                else 
                {
                    try
                    {
                        ParsedThumbnail thumbnail = new ParsedThumbnail();
                        thumbnail.EncodedImage = ReadImage(imageBuffer.ToString());
                        parsedFileInfo.Thumbnails.Add(thumbnail);
                    }
                    catch
                    {
                        //throw it away
                    }
                    return;
                }

            }
        }

        private static string ReadImage(string imageBuffer)
        {
            //Convert the string into a usable format
            var finalString = imageBuffer.Replace("Icon: ", String.Empty).Replace(";", string.Empty).Replace(" ", string.Empty).Replace("\r\n", string.Empty);
           
            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(finalString)))
            using (MemoryStream bitmapSource = new MemoryStream())
            {
                BinaryToImage(ms).Save(bitmapSource, System.Drawing.Imaging.ImageFormat.Png);
                return Convert.ToBase64String(bitmapSource.GetBuffer());
            }

        }

        /// <summary>
        /// Takes a memory stream containing the header icon + the 4 size bytes
        /// </summary>
        /// <param name="ms">memory stream containing the header icon + 4 size-bytes</param>
        /// <returns></returns>
        public static Bitmap BinaryToImage(MemoryStream ms)
        {
            ms.Position = 0;
            int width = ms.ReadByte() << 8 | ms.ReadByte();
            int height = ms.ReadByte() << 8 | ms.ReadByte();

            Bitmap target = new Bitmap(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte upper = (byte)ms.ReadByte();
                    byte lower = (byte)ms.ReadByte();
                    int color = lower | upper << 8;
                    byte[] rgb = RGB2To3Bytes(color);
                    Color pixel = Color.FromArgb(rgb[0], rgb[1], rgb[2]);
                    target.SetPixel(x, y, pixel);
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
