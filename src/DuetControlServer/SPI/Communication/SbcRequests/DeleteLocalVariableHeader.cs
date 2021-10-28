using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SbcRequests
{
    /// <summary>
    /// Header of a deletion request for local variables. This is followed by the variable name
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct DeleteLocalVariableHeader
    {
        /// <summary>
        /// Source of this request
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Length of the variable name
        /// </summary>
        public byte VariableLength;
    }
}
