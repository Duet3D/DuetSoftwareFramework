using System.Runtime.InteropServices;
using DuetAPI.Commands;

namespace DuetControlServer.SPI.Communication.DuetRequests
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
    }
}