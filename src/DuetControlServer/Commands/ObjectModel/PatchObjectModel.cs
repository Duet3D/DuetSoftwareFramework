using DuetAPI.ObjectModel;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.PatchObjectModel"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
public sealed class PatchObjectModel(Model.ObjectModel model, IOptions<Settings> settings) : DuetAPI.Commands.PatchObjectModel
{
    /// <summary>
    /// Apply a full patch to the object model. May be used only in non-SPI mode
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        #warning deprecate this?
        if (true)
        {
            throw new InvalidOperationException("Command is only supported in non-SPI mode");
        }

#if false
        if (model.UpdateFromJson(Key, Patch))
        {
            if (model.IsUpdating && model.State.Status != MachineStatus.Updating)
            {
                model.State.Status = MachineStatus.Updating;
            }
        }
        else
        {
            throw new ArgumentException($"Property '{Key}' not found");
        }

        return Task.CompletedTask;
#endif
    }
}
