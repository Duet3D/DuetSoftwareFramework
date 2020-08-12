using System.Collections.Generic;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing a loaded plugin
    /// </summary>
    public sealed class Plugin : PluginManifest
    {
        /// <summary>
        /// Process ID if the plugin or -1 if not started
        /// </summary>
        public int PID { get; set; } = -1;

        /// <summary>
        /// Dictionary holding key value pairs during the runtime of a plugin.
        /// May be used to share data between plugins or between the SBC and web interface
        /// </summary>
        public Dictionary<string, JsonElement> Data { get; set; } = new Dictionary<string, JsonElement>();
    }
}
