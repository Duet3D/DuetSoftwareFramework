using System.Runtime.InteropServices;
using DuetAPI;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request the execution of a macro file
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct MacroRequest
    {
        /// <summary>
        /// Channel to pipe the macro content into
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Output a warning message if the file could not be found
        /// </summary>
        public byte ReportMissing;

        /// <summary>
        /// Length of the filename
        /// </summary>
        public byte Length;
    }
}