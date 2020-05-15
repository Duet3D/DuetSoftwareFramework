using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request a code to be executed by DSF
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 4)]
    public struct DoCodeHeader
    {
        /// <summary>
        /// Channel number of the code
        /// </summary>
        [FieldOffset(0)]
        public CodeChannel Channel;

        /// <summary>
        /// Code length in bytes
        /// </summary>
        [FieldOffset(2)]
        public ushort Length;
    }
}
