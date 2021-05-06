using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Files.ImageProcessing
{
    public static class PrusaSlicerImageParser
    {
        public static async Task ProcessAsync(StreamReader reader, CodeParserBuffer codeParserBuffer, ParsedFileInfo parsedFileInfo, Code code)
        {

            ParsedThumbnail thumbnail = new ParsedThumbnail();
            int encodedLength = 0;
            StringBuilder encodedImage = new StringBuilder();

            //Read the image header info that is currently in the code
            string[] thumbnailTokens = code.Comment.Trim().Split(' ');

            //Stop processing since the thumbnail may be corrupt.
            if (thumbnailTokens.Length != 4)
            {
                throw new ImageProcessingException();
            }
            string[] dimensions = thumbnailTokens[2].Split('x');
            if (dimensions.Length != 2)
            {
                throw new ImageProcessingException();
            }

            thumbnail = new ParsedThumbnail
            {
                Width = int.Parse(dimensions[0]),
                Height = int.Parse(dimensions[1])
            };

            encodedLength = int.Parse(thumbnailTokens[3]);
            encodedImage.Clear();
            code.Reset();


            //Keep reading the data from the file
            while (codeParserBuffer.GetPosition(reader) < reader.BaseStream.Length)
            {
                Program.CancellationToken.ThrowIfCancellationRequested();
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
                    code.Reset();
                    continue;
                }

                if (code.Comment.Contains("thumbnail begin", StringComparison.InvariantCultureIgnoreCase))
                {
                    //Exit if we find another start tag before ending the previous image
                    throw new ImageProcessingException();
                }
                else if (code.Comment.Contains("thumbnail end", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (encodedImage.Length == encodedLength)
                    {
                        thumbnail.EncodedImage = "data:image/png;base64," + encodedImage.ToString();
                        parsedFileInfo.Thumbnails.Add(thumbnail);
                        return;
                    }
                }
                else
                {
                    encodedImage.Append(code.Comment.Trim());
                }
                code.Reset();
            }
        }
    }
}

