using System.Runtime.InteropServices;
using DuetAPI.Commands;

namespace DuetControlServer.SPI.Communication.DuetRequests
{
    /// <summary>
    /// Header for stack events (<see cref="Request.StackPushed"/> and <see cref="Request.StackPushed"/>)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 4)]
    public struct StackEvent
    {
        /// <summary>
        /// Code channel where the event occurred
        /// </summary>
        public CodeChannel Channel;
        
        /// <summary>
        /// New depth of the stack
        /// </summary>
        public byte StackDepth;
    }
}