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
        /// Channel which has locked or unlocked the resource
        /// </summary>
        public CodeChannel Channel;
    }
}
