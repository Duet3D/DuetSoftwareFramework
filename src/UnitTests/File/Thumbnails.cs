using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files.ImageProcessing;
using DuetControlServer.Files.Parser.ImageProcessing;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;

namespace UnitTests.File;

[TestFixture]
public class Thumbnails
{
    [TestCase("Thumbnail.gcode", ThumbnailInfoFormat.PNG, 2)]
    [TestCase("Thumbnail_JPG.gcode", ThumbnailInfoFormat.JPEG, 1)]
    [TestCase("Thumbnail_QOI.gcode", ThumbnailInfoFormat.QOI, 2)]
    public async Task ParseEmbedded(string fileName, ThumbnailInfoFormat format, int expectedCount)
    {
        GCodeFileInfo info = await ParseAsync(fileName, expectedCount);

        Assert.That(info.Thumbnails, Has.Count.EqualTo(expectedCount));
        foreach (ThumbnailInfo thumbnail in info.Thumbnails)
        {
            Assert.That(thumbnail.Format, Is.EqualTo(format));
            Assert.That(thumbnail.Width, Is.GreaterThan(0));
            Assert.That(thumbnail.Height, Is.GreaterThan(0));
            Assert.That(thumbnail.Size, Is.GreaterThan(0));
            Assert.That(thumbnail.Data, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public async Task ParseIcon()
    {
        GCodeFileInfo info = await ParseAsync("BenchyIcon.gcode", 1);

        Assert.That(info.Thumbnails, Has.Count.EqualTo(1));
        Assert.That(info.Thumbnails[0].Format, Is.EqualTo(ThumbnailInfoFormat.PNG));
        Assert.That(info.Thumbnails[0].Width, Is.EqualTo(320));
        Assert.That(info.Thumbnails[0].Height, Is.EqualTo(240));
        Assert.That(info.Thumbnails[0].Data, Is.Not.Null);

        // Icons are converted from RGB565 to PNG, so the reported dimensions must match the encoded ones
        byte[] png = Convert.FromBase64String(info.Thumbnails[0].Data!);
        Assert.That(png[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), Is.EqualTo(320));
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), Is.EqualTo(240));
    }

    /// <summary>
    /// Extract thumbnails from a test file the same way the file info parser does
    /// </summary>
    /// <param name="fileName">Name of the test file</param>
    /// <param name="expectedCount">Number of thumbnails to stop after, else the whole file would be parsed</param>
    /// <returns>File information holding the thumbnails that were found</returns>
    private static async Task<GCodeFileInfo> ParseAsync(string fileName, int expectedCount)
    {
        GCodeFileInfo result = new();
        await using FileStream stream = new(Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName), FileMode.Open, FileAccess.Read);
        CodeParserBuffer buffer = new(4096, true);

        Code code = new();
        while (result.Thumbnails.Count < expectedCount && buffer.GetPosition(stream) < stream.Length)
        {
            if (!await Code.ParseAsync(stream, code, buffer))
            {
                continue;
            }

            string trimmedComment = code.Comment?.TrimStart() ?? string.Empty;
            if (trimmedComment.StartsWith("thumbnail begin", StringComparison.InvariantCultureIgnoreCase))
            {
                await ImageParser.ProcessAsync(stream, buffer, result, code, true, ThumbnailInfoFormat.PNG, default);
            }
            else if (trimmedComment.StartsWith("thumbnail_JPG", StringComparison.InvariantCultureIgnoreCase))
            {
                await ImageParser.ProcessAsync(stream, buffer, result, code, true, ThumbnailInfoFormat.JPEG, default);
            }
            else if (trimmedComment.StartsWith("thumbnail_QOI", StringComparison.InvariantCultureIgnoreCase))
            {
                await ImageParser.ProcessAsync(stream, buffer, result, code, true, ThumbnailInfoFormat.QOI, default);
            }
            else if (trimmedComment.Contains("Icon:"))
            {
                await IconImageParser.ProcessAsync(stream, buffer, result, code, true, default);
            }
            code.Reset();
        }
        return result;
    }
}
