using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.SharedRequests
{
    /// <summary>
    /// Shared header for the <see cref="LinuxRequests.Request.GetObjectModel"/> and <see cref="FirmwareRequests.Request.ObjectModel"/> requests
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct ObjectModel
    {
        /// <summary>
        /// Number of the module that this response provides data for
        /// </summary>
        public byte Module;
    }
}