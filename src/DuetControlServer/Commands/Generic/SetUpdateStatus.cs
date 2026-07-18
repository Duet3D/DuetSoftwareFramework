using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SetUpdateStatus"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class SetUpdateStatus(Model.ObjectModel model) : DuetAPI.Commands.SetUpdateStatus
{
    /// <summary>
    /// Update the reported update status
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.IsUpdating = Updating;
            if (Updating)
            {
                model.SBC!.Upgrade ??= new();
                model.SBC.Upgrade.Message = Message;
                model.SBC.Upgrade.Progress = Progress;
            }
            else
            {
                model.SBC!.Upgrade = null;
            }
        }
    }
}
