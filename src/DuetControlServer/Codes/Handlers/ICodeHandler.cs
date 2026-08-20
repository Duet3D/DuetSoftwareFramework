using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Interface for code handlers that process codes sent to the control server
/// </summary>
public interface ICodeHandler
{
    /// <summary>
    /// The class of the given code, or null when this handler has no such code, in which case the
    /// pipeline runs the macro named after the code if one exists and resolves the code as
    /// unsupported otherwise. Side-effect free
    /// </summary>
    /// <param name="code">Code to classify</param>
    /// <returns>Class of the code, or null for "no such code"</returns>
    CodeClass? Classify(DuetAPI.Commands.Code code);

    /// <summary>
    /// Process a code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken);

    /// <summary>
    /// React to an executed code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken);
}
