using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.PatchObjectModel"/> command
    /// </summary>
    public sealed class PatchObjectModel : DuetAPI.Commands.PatchObjectModel
    {
        /// <summary>
        /// Apply a full patch to the object model. May be used only in non-SPI mode
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute()
        {
            if (!Settings.NoSpi)
            {
                throw new InvalidOperationException("Command is only supported in non-SPI mode");
            }

            if (!Model.Provider.Get.UpdateFromModel(Key, Patch))
            {
                throw new ArgumentException("Property not found", nameof(Key));
            }
            return Task.CompletedTask;
        }
    }
}
