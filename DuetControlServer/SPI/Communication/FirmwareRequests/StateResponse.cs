using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Response to a <see cref="LinuxRequests.Request.GetState"/> request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size =  4)]
    public struct StateResponse
    {
        /// <summary>
        /// Bitmap of the code channels that are currently busy
        /// </summary>
        public uint BusyChannels;
    }
}
