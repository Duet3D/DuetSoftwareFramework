namespace DuetAPI.Commands
{
    /// <summary>
    /// Enumeration of every available code channel
    /// <seealso cref="Machine.Channels.Model"/>
    /// </summary>
    public enum CodeChannel
    {
        /// <summary>
        /// Main G/M/T-code channel
        /// </summary>
        Main,

        /// <summary>
        /// Serial G/M/T-code channel (UART)
        /// </summary>
        Serial,

        /// <summary>
        /// G/M/T-code channel for files (print jobs)
        /// </summary>
        File,

        /// <summary>
        /// G/M/T-code channel for HTTP requests (DWC)
        /// </summary>
        HTTP,

        /// <summary>
        /// G/M/T-code channel for Telnet requests
        /// </summary>
        Telnet
    }
}
