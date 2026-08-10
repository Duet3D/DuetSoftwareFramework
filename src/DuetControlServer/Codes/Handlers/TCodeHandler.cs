using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Class that processes T-codes in the control server
/// </summary>
public sealed class TCodeHandler : ICodeHandler
{
    /// <summary>
    /// Process a T-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        return new Message(MessageType.Warning, "Not implemented yet");
    }

    /// <summary>
    /// React to an executed T-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
