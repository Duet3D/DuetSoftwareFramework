using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Model;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SyncObjectModel"/> command
/// </summary>
/// <param name="updateInterface">Update interface</param>
public sealed class SyncObjectModel(ObjectModel model) : DuetAPI.Commands.SyncObjectModel
{
    /// <summary>
    /// Waits for the machine model to be fully updated from RepRapFirmware
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override Task ExecuteAsync(CancellationToken cancellationToken = default) => model.WaitForFullUpdateAsync(cancellationToken);
}
