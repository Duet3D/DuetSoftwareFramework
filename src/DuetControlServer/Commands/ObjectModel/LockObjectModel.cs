using DuetControlServer.IPC;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.LockObjectModel"/> command
/// </summary>
/// <param name="lockManager">Lock manager</param>
public sealed class LockObjectModel(LockManager lockManager) : DuetAPI.Commands.LockObjectModel, IConnectionCommand
{
    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Lock the machine model for write access
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public override Task ExecuteAsync(CancellationToken cancellationToken) => lockManager.LockMachineModelAsync(Connection!, cancellationToken);
}
