using DuetControlServer.Utility;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace DuetControlServer.Files.ImageProcessing;

/// <summary>
/// Minimal encoder for 24-bit truecolor PNG images
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// Fixed signature every PNG file starts with
    /// </summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encode raw RGB pixel data as a PNG image
    /// </summary>
    /// <param name="pixels">Pixel data with three bytes per pixel in RGB order, row by row</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <returns>Encoded PNG image</returns>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height)
    {
        using MemoryStream result = new();
        result.Write(Signature);

        Span<byte> header = stackalloc byte[17];
        "IHDR"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], width);
        BinaryPrimitives.WriteInt32BigEndian(header[8..], height);
        header[12] = 8;     // bits per sample
        header[13] = 2;     // truecolor without alpha channel
        header[14] = 0;     // deflate compression
        header[15] = 0;     // adaptive filtering
        header[16] = 0;     // no interlacing
        WriteChunk(result, header);

        // Every scanline is prefixed with its filter type, see the Filter Algorithms section of RFC 2083
        using MemoryStream imageData = new();
        imageData.Write("IDAT"u8);
        using (ZLibStream deflater = new(imageData, CompressionLevel.Optimal, true))
        {
            int stride = 3 * width;
            for (int y = 0; y < height; y++)
            {
                deflater.WriteByte(0);
                deflater.Write(pixels.Slice(y * stride, stride));
            }
        }
        WriteChunk(result, imageData.GetBuffer().AsSpan(0, (int)imageData.Length));

        WriteChunk(result, "IEND"u8);
        return result.ToArray();
    }

    /// <summary>
    /// Write a single PNG chunk
    /// </summary>
    /// <param name="stream">Stream to write to</param>
    /// <param name="typeAndData">Four-character chunk type followed by the chunk payload, both of which the checksum is computed over</param>
    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> typeAndData)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, typeAndData.Length - 4);
        stream.Write(field);
        stream.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(field, CRC32.Calculate(typeAndData));
        stream.Write(field);
    }
}
