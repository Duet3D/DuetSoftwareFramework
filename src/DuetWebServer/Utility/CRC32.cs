using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Utility
{
    /// <summary>
    /// Functions for CRC32 calculation
    /// </summary>
    /// <remarks>
    /// The internals of this class originate from Crc32.NET, see https://github.com/force-net/Crc32.NET
    /// </remarks>
    public static class CRC32
    {
        private const uint Poly = 0xedb88320u;

        private static readonly uint[] _table = new uint[16 * 256];

        static CRC32()
        {
            var table = _table;
            for (uint i = 0; i < 256; i++)
            {
                uint res = i;
                for (int t = 0; t < 16; t++)
                {
                    for (int k = 0; k < 8; k++) res = (res & 1) == 1 ? Poly ^ (res >> 1) : (res >> 1);
                    table[(t * 256) + i] = res;
                }
            }
        }

        /// <summary>
        /// Calculate the CRC32 checksum for the given stream
        /// </summary>
        /// <param name="stream">Input stream</param>
        /// <param name="bufferSize">Size of the buffer to use</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>CRC32 checksum</returns>
        public static async Task<uint> Calculate(Stream stream, int bufferSize = 8192, CancellationToken cancellationToken = default)
        {
            int bytesRead, offset;
            uint crc = 0, crcLocal = uint.MaxValue ^ crc;

            byte[] buffer = new byte[bufferSize];
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                offset = 0;

                while (bytesRead >= 16)
                {
                    uint a = _table[(3 * 256) + buffer[offset + 12]]
                        ^ _table[(2 * 256) + buffer[offset + 13]]
                        ^ _table[(1 * 256) + buffer[offset + 14]]
                        ^ _table[(0 * 256) + buffer[offset + 15]];

                    uint b = _table[(7 * 256) + buffer[offset + 8]]
                        ^ _table[(6 * 256) + buffer[offset + 9]]
                        ^ _table[(5 * 256) + buffer[offset + 10]]
                        ^ _table[(4 * 256) + buffer[offset + 11]];

                    uint c = _table[(11 * 256) + buffer[offset + 4]]
                        ^ _table[(10 * 256) + buffer[offset + 5]]
                        ^ _table[(9 * 256) + buffer[offset + 6]]
                        ^ _table[(8 * 256) + buffer[offset + 7]];

                    uint d = _table[(15 * 256) + ((byte)crcLocal ^ buffer[offset])]
                        ^ _table[(14 * 256) + ((byte)(crcLocal >> 8) ^ buffer[offset + 1])]
                        ^ _table[(13 * 256) + ((byte)(crcLocal >> 16) ^ buffer[offset + 2])]
                        ^ _table[(12 * 256) + ((crcLocal >> 24) ^ buffer[offset + 3])];

                    crcLocal = d ^ c ^ b ^ a;
                    offset += 16;
                    bytesRead -= 16;
                }

                while (--bytesRead >= 0)
                {
                    crcLocal = _table[(byte)(crcLocal ^ buffer[offset++])] ^ crcLocal >> 8;
                }
            }

            return crcLocal ^ uint.MaxValue;
        }
    }
}
