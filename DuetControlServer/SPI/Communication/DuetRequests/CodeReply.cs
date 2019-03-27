using System.Runtime.InteropServices;
using DuetAPI.Commands;

namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Response to a <see cref="DuetControlServer.SPI.Communication.LinuxRequests.CodeHeader"/> request
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct CodeReply
    {
        /// <summary>
        /// Channel from which this message comes
        /// </summary>
        public CodeChannel Channel;
        
        /// <summary>
        /// Flags describing the content
        /// </summary>
        public CodeReplyFlags Flags;
    }
}