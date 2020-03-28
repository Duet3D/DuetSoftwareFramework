using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.Shared
{
    /// <summary>
    /// Header describing a response to a lock resource request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct LockUnlockHeader
    {
        /// <summary>
        /// Channel which has locked or unlocked the resource
        /// </summary>
        public CodeChannel Channel;
    }
}
