using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Utility;

/// <summary>
/// Helper class for the firmware fields
/// </summary>
public static class Firmware
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UF2BlockHeader
    {
        public uint MagicStart0;
        public uint MagicStart1;
        public uint Flags;
        public uint TargetAddr;
        public uint PayloadSize;
        public uint BlockNo;
        public uint NumBlocks;
        public uint FileSize;  // or FamilyID
    }
    private const int UF2DataOffset = 32;
    private const int UF2DataMaxLength = 476;
    private const int UF2MagicEndOffset = 508;

    private const uint MagicStart0 = 0x0A324655;
    private const uint MagicStart1 = 0x9E5D5157;
    private const uint MagicEnd = 0x0AB16F30;
    private const uint FlagNoFlash = 0x00000001;

    /// <summary>
    /// Unpack the first file from the given UF2 stream
    /// </summary>
    /// <param name="stream">Data stream</param>
    /// <returns>Unpacked file</returns>
    /// <exception cref="IOException">Invalid UF2 data</exception>
    public static async Task<MemoryStream> UnpackUF2Async(Stream stream)
    {
        if (stream.Length % 512 != 0)
        {
            throw new IOException("UF2 file size must be a multiple of 512 bytes");
        }

        MemoryStream result = new();

        Memory<byte> blockBuffer = new byte[512];
        UF2BlockHeader block;
        do
        {
            // Read another 512-byte segment
            if (await stream.ReadAsync(blockBuffer) < 512)
            {
                throw new IOException("Unexpected end in UF2 file");
            }

            // Cast it to a struct and verify the data
            block = MemoryMarshal.Cast<byte, UF2BlockHeader>(blockBuffer.Span)[0];
            if (block.MagicStart0 != MagicStart0 || block.MagicStart1 != MagicStart1)
            {
                throw new IOException("Invalid magic start in UF2 block");
            }

            uint magicEnd = MemoryMarshal.Read<uint>(blockBuffer.Slice(UF2MagicEndOffset, sizeof(uint)).Span);
            if (magicEnd != MagicEnd)
            {
                throw new IOException("Invalid magic end in UF2 block");
            }

            if (block.PayloadSize > UF2DataMaxLength)
            {
                throw new IOException("Invalid payload size in UF2 block");
            }

            // Write the block payload to the result
            if (block.Flags != FlagNoFlash)
            {
                await result.WriteAsync(blockBuffer.Slice(UF2DataOffset, (int)block.PayloadSize));
            }
        }
        while (block.BlockNo + 1 < block.NumBlocks);

        result.Seek(0, SeekOrigin.Begin);
        return result;
    }

    /// <summary>
    /// Offset in the firmware file pointing to the firmware identifier
    /// </summary>
    private const int FirmwareIdentifierOffset = 0x20;

    /// <summary>
    /// Offset in the firmware file where the load address is stored
    /// </summary>
    private const int FirmwareLoadOffset = 0x24;

    /// <summary>
    /// Maximum length of a possible firmware identifier string
    /// </summary>
    private const int MaxFirmwareStringLength = 128;

    /// <summary>
    /// Try to read the firmware version from a given firmware file
    /// </summary>
    /// <param name="filename">Firmware file</param>
    /// <param name="bufferSize">Buffer size for reading the file</param>
    /// <returns>Firmware version or null if not found</returns>
    public static async Task<string?> GetFirmwareVersionAsync(string filename, int bufferSize)
    {
        Stream? firmwareFile = null;
        try
        {
            // Get a stream containing the binary content
            if (Path.GetExtension(filename) == ".uf2")
            {
                await using FileStream fs = new(filename, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
                firmwareFile = await UnpackUF2Async(fs);
            }
            else
            {
                firmwareFile = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
            }

            // Check if we can read the version and load addresses
            if (firmwareFile.Length < Math.Max(FirmwareIdentifierOffset, FirmwareLoadOffset) + sizeof(uint))
            {
                return null;
            }

            // Read the identifier and load and start offsets
            using BinaryReader reader = new(firmwareFile, Encoding.UTF8);
            firmwareFile.Seek(FirmwareIdentifierOffset, SeekOrigin.Begin);
            uint versionAddress = reader.ReadUInt32();
            firmwareFile.Seek(FirmwareLoadOffset, SeekOrigin.Begin);
            uint loadAddress = reader.ReadUInt32();

            // Attempt to retrieve the firmware identifier
            if (versionAddress > loadAddress && versionAddress - loadAddress < firmwareFile.Length)
            {
                firmwareFile.Seek(versionAddress - loadAddress, SeekOrigin.Begin);

                int numCharsRead = 0;
                StringBuilder builder = new();
                while (firmwareFile.CanRead)
                {
                    char c = reader.ReadChar();
                    if (c == '\0')
                    {
                        // Reached end of string
                        break;
                    }
                    if (numCharsRead++ >= MaxFirmwareStringLength)
                    {
                        // Overflow, result is invalid
                        return null;
                    }
                    if (c == ' ')
                    {
                        // We're only interested in the last space-delimited item 
                        builder.Clear();
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }
                return (builder.Length > 0) ? builder.ToString() : null;
            }
        }
        finally
        {
            if (firmwareFile is not null)
            {
                await firmwareFile.DisposeAsync();
            }
        }
        return null;
    }
}
