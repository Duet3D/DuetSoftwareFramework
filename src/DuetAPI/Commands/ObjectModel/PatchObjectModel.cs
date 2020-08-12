using DuetAPI.Utility;
using System;
using System.Text.Json;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Apply a full patch to the object model. May be used only in non-SPI mode
    /// </summary>
    /// <exception cref="ArgumentException">Invalid key specified</exception>
    /// <exception cref="InvalidOperationException">DCS is not running in non-SPI mode</exception>
    /// <seealso cref="LockObjectModel"/>
    /// <seealso cref="UnlockObjectModel"/>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class PatchObjectModel : Command
    {
        /// <summary>
        /// Key to update
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// JSON patch to apply
        /// </summary>
        public JsonElement Patch { get; set; }
    }
}
