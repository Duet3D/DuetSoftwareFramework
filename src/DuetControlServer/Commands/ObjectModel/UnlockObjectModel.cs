using DuetControlServer.IPC;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UnlockObjectModel"/> command
/// </summary>
/// <param name="lockManager">Lock manager</param>
public sealed class UnlockObjectModel(LockManager lockManager) : DuetAPI.Commands.UnlockObjectModel, IConnectionCommand
{
    /// <summary>
    /// Source connection of this command. Needed to register the owner of the lock
    /// </summary>
    public Connection? Connection { get; set; }

    /// <summary>
    /// Unlock the machine model again
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        lockManager.UnlockMachineModel(Connection!);
        return Task.CompletedTask;
    }
}
