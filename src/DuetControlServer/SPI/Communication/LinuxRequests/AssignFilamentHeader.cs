using System.Runtime.InteropServices;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Header of a filament assignment. This is followed by the actual filament name
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct AssignFilamentHeader
    {
        /// <summary>
        /// Extruder drive number
        /// </summary>
        public int Extruder;

        /// <summary>
        /// Length of the filament name
        /// </summary>
        public uint FilamentLength;
    }
}
