using System.Runtime.InteropServices;
using DuetAPI;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Header for G/M/T-codes
    /// </summary>
    /// <remarks>
    /// This is followed by NumParameters <see cref="CodeParameter"/> instances,
    /// which is then followed by concatenated zero-terminated UTF8-strings for each parameter where applicable
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct CodeHeader
    {
        /// <summary>
        /// Target of the code
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Flags of this code
        /// </summary>
        public CodeFlags Flags;

        /// <summary>
        /// Number of parameters following the 
        /// </summary>
        public byte NumParameters;

        /// <summary>
        /// Letter of this code (G/M/T)
        /// </summary>
        public byte Letter;
        
        /// <summary>
        /// Major code number (e.g. 1 in G1)
        /// </summary>
        public int MajorCode;

        /// <summary>
        /// Minor code number (e.g. 4 in G53.4)
        /// </summary>
        public int MinorCode;

        /// <summary>
        /// File position after the read code. This is used for pausing and resuming
        /// </summary>
        public uint FilePosition;
    }
}