using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Helper class for the UF2 file format
    /// </summary>
    public static class UF2
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
        public static async Task<MemoryStream> Unpack(Stream stream)
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
    }
}
