﻿using System.Text.Json.Serialization;

namespace DuetAPI
{
    /// <summary>
    /// Enumeration of supported input channel names
    /// </summary>
    /// <remarks>
    /// The indices of this enum are tightly coupled with RepRapFirmware.
    /// Make sure to update this enum accordingly whenever changes are made to it!
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodeChannel : byte
    {
        /// <summary>
        /// Code channel for HTTP requests
        /// </summary>
        HTTP = 0,

        /// <summary>
        /// Code channel for Telnet requests
        /// </summary>
        Telnet = 1,

        /// <summary>
        /// Code channel for file prints
        /// </summary>
        File = 2,

        /// <summary>
        /// Code channel for USB requests
        /// </summary>
        USB = 3,

        /// <summary>
        /// Code channel for serial devices (e.g. PanelDue)
        /// </summary>
        Aux = 4,

        /// <summary>
        /// Code channel for running triggers or config.g
        /// </summary>
        Trigger = 5,

        /// <summary>
        /// Code channel for the code queue that executes a couple of codes in-sync with moves
        /// </summary>
        Queue = 6,

        /// <summary>
        /// Code channel for auxiliary LCD devices (e.g. PanelOne)
        /// </summary>
        LCD = 7,

        /// <summary>
        /// Default code channel for requests over SPI
        /// </summary>
        SBC = 8,

        /// <summary>
        /// Code channel that executes the daemon process
        /// </summary>
        Daemon = 9,

        /// <summary>
        /// Code channel for the second UART port
        /// </summary>
        Aux2 = 10,

        /// <summary>
        /// Code channel that executes macros on power fail, heater faults and filament out
        /// </summary>
        Autopause = 11,

        /// <summary>
        /// Unknown code channel
        /// </summary>
        Unknown = 12
    }
}
