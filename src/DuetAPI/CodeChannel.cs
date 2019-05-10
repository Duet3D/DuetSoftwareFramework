namespace DuetAPI
{
    /// <summary>
    /// Enumeration of every available code channel
    /// </summary>
    /// <seealso cref="Commands.Code"/>
    /// <seealso cref="Commands.SimpleCode"/>
    /// <seealso cref="Machine.Channels"/>
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
        AUX = 4,

        /// <summary>
        /// Code channel for running triggers or config.g
        /// </summary>
        Daemon = 5,

        /// <summary>
        /// Code channel for the code queue that executes a couple of codes in-sync with moves
        /// </summary>
        CodeQueue = 6,

        /// <summary>
        /// Code channel for auxiliary LCD devices (e.g. PanelOne)
        /// </summary>
        LCD = 7,

        /// <summary>
        /// Default code channel for requests over SPI
        /// </summary>
        SPI = 8,

        /// <summary>
        /// Code channel that executes macros on power fail, heater faults and filament out
        /// </summary>
        AutoPause = 9
    }
}
