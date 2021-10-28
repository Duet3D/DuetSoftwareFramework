using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Query the object model
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct GetObjectModelHeader
    {
        /// <summary>
        /// Type of the value
        /// </summary>
        public ushort KeyLength;

        /// <summary>
        /// Length of the payload
        /// </summary>
        public ushort FlagsLength;
    }
}
