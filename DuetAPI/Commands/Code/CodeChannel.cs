namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of every available code channel
    /// <seealso cref="Machine.Channels.Model"/>
    /// </summary>
    public enum CodeChannel : byte
    {
        /// <summary>
        /// Main G/M/T-code channel
        /// </summary>
        Main = 0,

        /// <summary>
        /// Serial G/M/T-code channel (UART)
        /// </summary>
        Serial = 1,

        /// <summary>
        /// G/M/T-code channel for files (print jobs)
        /// </summary>
        File = 2,

        /// <summary>
        /// G/M/T-code channel for HTTP requests (DWC)
        /// </summary>
        HTTP = 3,

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        Telnet = 4
    }
}
