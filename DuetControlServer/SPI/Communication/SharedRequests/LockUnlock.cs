using DuetAPI.Commands;
using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Header describing a response to a lock resource request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct LockUnlock
    {
        /// <summary>
        /// Channel which has locked or unlocked the resource
        /// </summary>
        public CodeChannel Channel;
    }
}
