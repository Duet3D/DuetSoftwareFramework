using DuetAPI.Connection;

namespace DuetWebServer
{
    /// <summary>
    /// This class holds settings for DuetWebServer
    /// </summary>
    public sealed class Settings
    {
        /// <summary>
        /// Default directory to serve web content from
        /// </summary>
        public string DefaultWebDirectory { get; set; } = "/opt/dsf/sd/www";

        /// <summary>
        /// Keep-alive interval for WebSocket connections (in s)
        /// </summary>
        public int KeepAliveInterval { get; set; } = 30;

        /// <summary>
        /// Maximum age of cached resources before they must be refreshed (in s)
        /// </summary>
        /// <remarks>
        /// The index file is never cached to allow browsers to detect changes after updates
        /// </remarks>
        public int MaxAge { get; set; } = 3600;

        /// <summary>
        /// Time to wait before attempting to subscribe to the DSF OM again (in ms)
        /// </summary>
        public int ModelRetryDelay { get; set; } = 5000;

        /// <summary>
        /// Timeout for web sessions (in ms)
        /// </summary>
        public int SessionTimeout { get; set; } = 8000;

        /// <summary>
        /// Full filename of the DSF IPC socket to use
        /// </summary>
        public string SocketPath { get; set; } = Defaults.FullSocketPath;

        /// <summary>
        /// File holding the last known start-up error of DCS in case it failed to start
        /// </summary>
        public string StartErrorFile { get; set; } = Defaults.StartErrorFile;

        /// <summary>
        /// Provide static settings from 0:/www
        /// </summary>
        public bool UseStaticFiles { get; set; } = true;

        /// <summary>
        /// Buffer size for custom WebSocket connections
        /// </summary>
        public int WebSocketBufferSize { get; set; } = 8192;
    }
}