using DuetControlServer.Codes;
using DuetControlServer.IPC;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.Flush"/> command
/// </summary>
/// <param name="codeProcessor">Code processor</param>
/// <param name="model">Object model</param>
public sealed class Flush(CodeProcessor codeProcessor, Model.ObjectModel model) : DuetAPI.Commands.Flush, IConnectionCommand
{
    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Wait for all pending codes of the given channel to finish
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Check if the corresponding code channel has been disabled
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Inputs[Channel] is null)
            {
                throw new InvalidOperationException("Requested code channel has been disabled");
            }
        }

        // Wait for it to be flushed
        Code? codeBeingIntercepted = IPC.Processors.CodeInterception.GetCodeBeingIntercepted(Connection, out _);
        return await ((codeBeingIntercepted is not null) ? codeProcessor.FlushAsync(codeBeingIntercepted, false, SyncFileStreams, IfExecuting, cancellationToken) : codeProcessor.FlushAsync(Channel, cancellationToken: cancellationToken));
    }
}
