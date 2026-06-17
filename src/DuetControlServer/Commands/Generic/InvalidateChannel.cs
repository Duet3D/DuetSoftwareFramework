using DuetControlServer.IPC;
using DuetControlServer.Link;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.InvalidateChannel"/> command
/// </summary>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
public sealed class InvalidateChannel(LinkInterface linkInterface, Model.ObjectModel model) : DuetAPI.Commands.InvalidateChannel, IConnectionCommand
{
    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Wait for all pending codes of the given channel to finish
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Check if the corresponding code channel has been disabled
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Inputs[Channel] is null)
            {
                throw new InvalidOperationException("Requested code channel has been disabled");
            }
        }

        // Wait for all codes and files to be invalidated
        await linkInterface.AbortAllAsync(Channel, cancellationToken);
    }
}
