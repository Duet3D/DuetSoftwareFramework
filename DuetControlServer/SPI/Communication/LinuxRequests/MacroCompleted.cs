using System.Runtime.InteropServices;
using DuetAPI.Commands;

namespace DuetControlServer.SPI.Communication.LinuxRequests
{
    /// <summary>
    /// Indicate that a macro has finished its execution
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct MacroCompleted
    {
        /// <summary>
        /// Channel on which the execution was done
        /// </summary>
        public CodeChannel Channel;

        /// <summary>
        /// Error flag. This is true if the file could not be found or opened
        /// </summary>
        public byte Error;
    }
}