using DuetAPI.Utility;
using System;
using System.Text.Json;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Update custom plugin data in the object model
    /// </summary>
    /// <remarks>
    /// May be used to update only the own plugin data unless the plugin has the <see cref="SbcPermissions.ManagePlugins"/> permission.
    /// Note that the corresponding key must already exist in the plugin data!
    /// </remarks>
    /// <exception cref="ArgumentException">Invalid plugin name or data key specified</exception>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class SetPluginData : Command
    {
        /// <summary>
        /// Identifier of the plugin to update (optional)
        /// </summary>
        public string Plugin { get; set; }

        /// <summary>
        /// Key to set
        /// </summary>
        /// <remarks>
        /// This key must already exist in the <see cref="ObjectModel.PluginManifest.Data"/> object!
        /// </remarks>
        public string Key { get; set; }

        /// <summary>
        /// Custom value to set
        /// </summary>
        public JsonElement Value { get; set; }
    }
}
