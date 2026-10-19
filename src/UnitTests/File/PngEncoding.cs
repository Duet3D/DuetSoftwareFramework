using DuetControlServer.Files.ImageProcessing;
using DuetControlServer.Utility;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UnitTests.File;

[TestFixture]
public class PngEncoding
{
    [Test]
    public void Encode()
    {
        byte[] pixels =
        [
            0xFF, 0x00, 0x00,  0x00, 0xFF, 0x00,  0x00, 0x00, 0xFF,
            0xFF, 0xFF, 0xFF,  0x80, 0x80, 0x80,  0x00, 0x00, 0x00
        ];
        byte[] png = PngWriter.Encode(pixels, 3, 2);

        Assert.That(png[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));

        // Walk the chunks and verify each checksum along the way
        MemoryStream imageData = new();
        List<string> chunkTypes = [];
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string chunkType = Encoding.ASCII.GetString(png, offset + 4, 4);
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length)), Is.EqualTo(CRC32.Calculate(png.AsSpan(offset + 4, 4 + length))), $"bad checksum in {chunkType} chunk");

            if (chunkType == "IHDR")
            {
                Assert.That(length, Is.EqualTo(13));
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8)), Is.EqualTo(3));
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12)), Is.EqualTo(2));
                Assert.That(png[offset + 16], Is.EqualTo(8), "bits per sample");
                Assert.That(png[offset + 17], Is.EqualTo(2), "colour type");
                Assert.That(png[offset + 18], Is.EqualTo(0), "compression method");
                Assert.That(png[offset + 19], Is.EqualTo(0), "filter method");
                Assert.That(png[offset + 20], Is.EqualTo(0), "interlace method");
            }
            else if (chunkType == "IDAT")
            {
                imageData.Write(png, offset + 8, length);
            }
            else
            {
                Assert.That(chunkType, Is.EqualTo("IEND"));
                Assert.That(length, Is.EqualTo(0));
            }

            chunkTypes.Add(chunkType);
            offset += 12 + length;
        }
        Assert.That(offset, Is.EqualTo(png.Length), "trailing data");
        Assert.That(chunkTypes, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));

        // Inflate the image data again and compare it to the original pixels
        imageData.Position = 0;
        using ZLibStream inflater = new(imageData, CompressionMode.Decompress);
        MemoryStream scanlines = new();
        inflater.CopyTo(scanlines);
        Assert.That(scanlines.Length, Is.EqualTo(2 * (1 + 3 * 3)));
        for (int y = 0; y < 2; y++)
        {
            Assert.That(scanlines.GetBuffer()[y * 10], Is.EqualTo(0), "filter type");
            Assert.That(scanlines.GetBuffer()[(y * 10 + 1)..(y * 10 + 10)], Is.EqualTo(pixels[(y * 9)..(y * 9 + 9)]));
        }
    }
}
