using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Body for a request that only contains a string value
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct StringHeader
    {
        /// <summary>
        /// Length of the following expression
        /// </summary>
        public ushort Length;
    }
}
