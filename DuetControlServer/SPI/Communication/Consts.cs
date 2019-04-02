using DuetAPI.Commands;
using System;

namespace DuetControlServer.SPI.Communication
{
    /// <summary>
    /// Static class holding SPI transfer constants
    /// </summary>
    public static class Consts
    {
        /// <summary>
        /// Unique format code for binary SPI transfers
        /// </summary>
        /// <remarks>Must be different from any other used format code (0x3E = DuetWiFiServer)</remarks>
        public const byte FormatCode = 0x5F;

        /// <summary>
        /// Unique format code that is not used anywhere else
        /// </summary>
        public const byte InvalidFormatCode = 0xC9;
        
        /// <summary>
        /// Used protocol version. This is incremented whenever the protocol details change
        /// </summary>
        public const ushort ProtocolVersion = 1;

        /// <summary>
        /// Size of a packet transfer buffer
        /// </summary>
        public const int BufferSize = 2048;

        /// <summary>
        /// Number of code channels
        /// </summary>
        /// <seealso cref="CodeChannel"/>
        public const byte NumCodeChannels = 10;
        
        /// <summary>
        /// Number of RepRapFirmware modules that can be queried via <see cref="LinuxRequests.Request.GetObjectModel"/>.
        /// This equals the number of RepRapFirmware modules minus 1 (because LinuxComm does not have an object model)
        /// </summary>
        public const byte NumModules = 16;

        /// <summary>
        /// Maximum size of a binary encoded G/M/T-code. This is limited by RepRapFirmware
        /// </summary>
        public const int MaxCodeBufferSize = 192;
    }

    /// <summary>
    /// Reasons why a print has been paused
    /// </summary>
    public enum PrintPausedReason : byte
    {
        /// <summary>
        /// User-initiated pause (M26)
        /// </summary>
        User = 1,

        /// <summary>
        /// G-Code initiated pause (M226)
        /// </summary>
        GCode = 2,

        /// <summary>
        /// Filament change required (M600)
        /// </summary>
        FilamentChange = 3,

        /// <summary>
        /// Paused by trigger
        /// </summary>
        Trigger = 4,

        /// <summary>
        /// Paused due to heater fault
        /// </summary>
        HeaterFault = 5,

        /// <summary>
        /// Paused because of a filament sensor
        /// </summary>
        Filament = 6,

        /// <summary>
        /// Paused due to a motor stall
        /// </summary>
        Stall = 7,

        /// <summary>
        /// Paused due to a voltage drop
        /// </summary>
        LowVoltage = 8
    }

}