using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SyncObjectModel"/> command
/// </summary>
public sealed class SyncObjectModel : DuetAPI.Commands.SyncObjectModel
{
    /// <summary>
    /// Complete at once: DuetControlServer keeps the object model itself, so a caller that holds the
    /// read lock already sees every effect that has happened, and there is no separate copy for this
    /// to wait to catch up with
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
