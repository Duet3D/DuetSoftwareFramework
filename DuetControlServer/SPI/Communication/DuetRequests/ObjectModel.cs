using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Response to a <see cref="DuetControlServer.SPI.Communication.LinuxRequests.GetObjectModel"/> request
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