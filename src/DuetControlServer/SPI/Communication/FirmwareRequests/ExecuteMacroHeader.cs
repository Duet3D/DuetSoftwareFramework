using System.Runtime.InteropServices;
using DuetAPI;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Request execution of a macro file
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ExecuteMacroHeader
    {
        /// <summary>
        /// Channel to pipe the macro content into
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Used to be ReportMissing but this is no longer needed
        /// </summary>
        public byte Dummy;

        /// <summary>
        /// Whether the code was requested from a G/M/T-code
        /// </summary>
        public byte FromCode;

        /// <summary>
        /// Length of the filename
        /// </summary>
        public byte Length;
    }
}