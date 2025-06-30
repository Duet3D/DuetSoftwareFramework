using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// G-code handler
/// </summary>
public sealed class GCodeHandler : ICodeHandler
{
    /// <summary>
    /// Process a G-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    public ValueTask<Message?> ProcessAsync(Commands.Code code) => ValueTask.FromResult<Message?>(null);

    /// <summary>
    /// React to an executed G-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code) => ValueTask.CompletedTask;
}
