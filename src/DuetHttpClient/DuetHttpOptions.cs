using System;

namespace DuetHttpClient
{
    /// <summary>
    /// Class for storing the connection defaults for Duet HTTP clients
    /// </summary>
    public class DuetHttpOptions
    {
        /// <summary>
        /// Keep-alive interval for WebSockets (only used in SBC mode)
        /// </summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum number of HTTP retries
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Defines whether messages are supposed to be observed.
        /// If this is true, messages are added to the object model and they must be cleared manually
        /// </summary>
        public bool ObserveMessages { get; set; }

        /// <summary>
        /// Defines whether the full object model is supposed to be observed
        /// </summary>
        public bool ObserveObjectModel { get; set; } = true;

        /// <summary>
        /// Password for the remote device
        /// </summary>
        public string Password { get; set; } = "reprap";

        /// <summary>
        /// WebSocket PING interval (only used in SBC mode)
        /// </summary>
        public TimeSpan PingInterval { get; set; } = TimeSpan.FromMilliseconds(2000);

        /// <summary>
        /// Interval at which the remote machine is queried to keep the HTTP session alive.
        /// This is only used if the object model is not queried
        /// </summary>
        public TimeSpan SessionKeepAliveInterval { get; set; } = TimeSpan.FromMilliseconds(4000);

        /// <summary>
        /// Default HTTP request timeout
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(4000);

        /// <summary>
        /// Time to wait after an object model update has been received (only used in SBC mode)
        /// </summary>
        public TimeSpan UpdateDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Interval of object model updates (only used in standalone mode)
        /// </summary>
        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    }
}
