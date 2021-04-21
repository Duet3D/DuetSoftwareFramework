using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header holding a G-code channel
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct CodeChannelHeader
    {
        /// <summary>
        /// Channel of the corresponding request
        /// </summary>
        public CodeChannel Channel;
    }
}
