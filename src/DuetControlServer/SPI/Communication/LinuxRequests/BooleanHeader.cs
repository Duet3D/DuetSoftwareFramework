using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Plain boolean value
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct BooleanHeader
    {
        /// <summary>
        /// Boolean value as byte
        /// </summary>
        public byte Value;
    }
}
