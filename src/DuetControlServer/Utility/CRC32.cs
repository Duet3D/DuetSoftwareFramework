using System;

namespace DuetControlServer.Utility
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
		/// Calculate the CRC32 checksum for the given byte span
		/// </summary>
		/// <param name="buffer">Input data</param>
		/// <returns>CRC32 checksum</returns>
		public static uint Calculate(Span<byte> buffer)
		{
			uint crc = 0;
			int length = buffer.Length, offset = 0;

			uint crcLocal = uint.MaxValue ^ crc;

			uint[] table = _table;
			while (length >= 16)
			{
				var a = table[(3 * 256) + buffer[offset + 12]]
					^ table[(2 * 256) + buffer[offset + 13]]
					^ table[(1 * 256) + buffer[offset + 14]]
					^ table[(0 * 256) + buffer[offset + 15]];

				var b = table[(7 * 256) + buffer[offset + 8]]
					^ table[(6 * 256) + buffer[offset + 9]]
					^ table[(5 * 256) + buffer[offset + 10]]
					^ table[(4 * 256) + buffer[offset + 11]];

				var c = table[(11 * 256) + buffer[offset + 4]]
					^ table[(10 * 256) + buffer[offset + 5]]
					^ table[(9 * 256) + buffer[offset + 6]]
					^ table[(8 * 256) + buffer[offset + 7]];

				var d = table[(15 * 256) + ((byte)crcLocal ^ buffer[offset])]
					^ table[(14 * 256) + ((byte)(crcLocal >> 8) ^ buffer[offset + 1])]
					^ table[(13 * 256) + ((byte)(crcLocal >> 16) ^ buffer[offset + 2])]
					^ table[(12 * 256) + ((crcLocal >> 24) ^ buffer[offset + 3])];

				crcLocal = d ^ c ^ b ^ a;
				offset += 16;
				length -= 16;
			}

			while (--length >= 0)
				crcLocal = table[(byte)(crcLocal ^ buffer[offset++])] ^ crcLocal >> 8;

			return crcLocal ^ uint.MaxValue;
		}
	}
}
