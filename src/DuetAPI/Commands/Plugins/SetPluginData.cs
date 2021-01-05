using DuetAPI.Utility;
using System;
using System.Text.Json;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Set custom plugin data in the object model
    /// </summary>
    /// <remarks>
    /// May be used to update only the own plugin data unless the plugin has the <see cref="SbcPermissions.ManagePlugins"/> permission
    /// </remarks>
    /// <exception cref="ArgumentException">Invalid plugin name specified</exception>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class SetPluginData : Command
    {
        /// <summary>
        /// Name of the plugin to update (optional)
        /// </summary>
        public string Plugin { get; set; }

        /// <summary>
        /// Key to set
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Value to set
        /// </summary>
        public JsonElement Value { get; set; }
    }
}
