namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of every available code channel
    /// <seealso cref="Machine.Channels.Model"/>
    /// </summary>
    public enum CodeChannel : byte
    {
        /// <summary>
        /// G/M/T-code channel for files (print jobs)
        /// </summary>
        File = 0,

        /// <summary>
        /// G/M/T-code channel for HTTP requests (DWC)
        /// </summary>
        HTTP = 1,

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        Telnet = 2,

        /// <summary>
        /// Main G/M/T-code channel
        /// </summary>
        SPI = 3
    }
}
