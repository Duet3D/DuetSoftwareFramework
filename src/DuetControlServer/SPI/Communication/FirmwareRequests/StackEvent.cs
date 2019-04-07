using System;
using System.Runtime.InteropServices;
using DuetAPI.Commands;

namespace DuetControlServer.SPI.Communication.FirmwareRequests
{
    /// <summary>
    /// Flags of the stack
    /// </summary>
    [Flags]
    public enum StackFlags
    {
        /// <summary>
        /// Whether the extruder drives are driven in relative mode
        /// </summary>
        DrivesRelative = 1,

        /// <summary>
        /// Whether the axes are driven in relative mode
        /// </summary>
        AxesRelative = 2,

        /// <summary>
        /// Whether the configured unit is set to inches
        /// </summary>
        UsingInches = 4
    }

    /// <summary>
    /// Header for stack events
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct StackEvent
    {
        /// <summary>
        /// Code channel where the event occurred
        /// </summary>
        public byte Channel;
        
        /// <summary>
        /// New depth of the stack
        /// </summary>
        public byte StackDepth;

        /// <summary>
        /// Stack flags
        /// </summary>
        /// <seealso cref="StackFlags"/>
        public ushort Flags;

        /// <summary>
        /// Feedrate in mm/s
        /// </summary>
        public float Feedrate;
    }
}