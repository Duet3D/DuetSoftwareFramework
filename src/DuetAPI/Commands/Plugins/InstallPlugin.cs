using DuetAPI.Utility;
using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Install or upgrade a plugin
    /// </summary>
    /// <exception cref="ArgumentException">Plugin is incompatible</exception>
    [RequiredPermissions(SbcPermissions.ManagePlugins)]
    public class InstallPlugin : Command
    {
        /// <summary>
        /// Absolute file path to the plugin ZIP bundle
        /// </summary>
        public string PluginFile { get; set; }
    }
}
