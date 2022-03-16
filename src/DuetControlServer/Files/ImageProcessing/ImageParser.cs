using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetControlServer.Files.ImageProcessing
{
    /// <summary>
    /// Functions for parsing embedded thumbnail images
    /// </summary>
    public static class ImageParser
    {
        /// <summary>
        /// Regex to test if a string is valid base64 data
        /// </summary>
        private static readonly Regex Base64Regex = new(@"^[A-Za-z0-9+/=]+$", RegexOptions.Compiled);

        /// <summary>
        /// Extract thumbnails images from a file
        /// </summary>
        /// <param name="reader">Stream reader to read from</param>
        /// <param name="codeParserBuffer">Parser buffer</param>
        /// <param name="parsedFileInfo">File information</param>
        /// <param name="code">Code to reuse while parsing</param>
        /// <param name="readThumbnailContent">Whether thumbnail content shall be returned</param>
        /// <param name="format">Thumbnail format</param>
        /// <returns>Asynchronous task</returns>
        public static async ValueTask ProcessAsync(StreamReader reader, CodeParserBuffer codeParserBuffer, GCodeFileInfo parsedFileInfo, Code code, bool readThumbnailContent, ThumbnailInfoFormat format)
        {
            // Read the image header info that is currently in the code
            string[] thumbnailTokens = code.Comment.Trim().Split(' ');
            if (thumbnailTokens.Length != 4)
            {
                throw new ImageProcessingException();
            }

            // Get the dimensions and encoded length
            string[] dimensions = thumbnailTokens[^2].Split('x');
            if (dimensions.Length != 2)
            {
                throw new ImageProcessingException();
            }

            int encodedLength = int.Parse(thumbnailTokens[^1]);
            ThumbnailInfo thumbnail = new()
            {
                Format = format,
                Width = int.Parse(dimensions[0]),
                Height = int.Parse(dimensions[1]),
                Offset = codeParserBuffer.GetPosition(reader),
                Size = encodedLength
            };

            // Keep reading the data from the file
            bool offsetAdjusted = false;
            StringBuilder data = readThumbnailContent ? new(encodedLength) : null;
            while (codeParserBuffer.GetPosition(reader) < reader.BaseStream.Length)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();

                code.Reset();
                if (!await Code.ParseAsync(reader, code, codeParserBuffer))
                {
                    continue;
                }
                if (code.Type != CodeType.Comment)
                {
                    return;
                }
                if (string.IsNullOrEmpty(code.Comment))
                {
                    continue;
                }

                string trimmedComment = code.Comment.Trim();
                if (trimmedComment.StartsWith("thumbnail begin", StringComparison.InvariantCultureIgnoreCase) ||
                    trimmedComment.StartsWith("thumbnail_JPG begin", StringComparison.InvariantCultureIgnoreCase) ||
                    trimmedComment.StartsWith("thumbnail_QOI begin", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Exit if we find another start tag before ending the previous image
                    throw new ImageProcessingException();
                }

                if (trimmedComment.StartsWith("thumbnail end", StringComparison.InvariantCultureIgnoreCase) ||
                    trimmedComment.StartsWith("thumbnail_JPG end", StringComparison.InvariantCultureIgnoreCase) ||
                    trimmedComment.StartsWith("thumbnail_QOI end", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (readThumbnailContent)
                    {
                        string dataContent = data.ToString();
                        thumbnail.Data = Base64Regex.IsMatch(dataContent) ? dataContent : null;
                    }
                    parsedFileInfo.Thumbnails.Add(thumbnail);
                    return;
                }
                else
                {
                    if (!offsetAdjusted)
                    {
                        thumbnail.Offset++;     // for leading semicolon
                        foreach (char c in code.Comment)
                        {
                            if (char.IsWhiteSpace(c))
                            {
                                thumbnail.Offset++;
                            }
                            else
                            {
                                break;
                            }
                        }
                        offsetAdjusted = true;
                    }

                    if (readThumbnailContent)
                    {
                        data.Append(trimmedComment);
                    }
                }
            }
        }
    }
}
