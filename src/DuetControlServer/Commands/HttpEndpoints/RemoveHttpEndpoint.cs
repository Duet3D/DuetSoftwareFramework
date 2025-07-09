using DuetAPI.ObjectModel;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.RemoveHttpEndpoint"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="logger">Logger instance</param>
public sealed class RemoveHttpEndpoint(Model.ObjectModel model, ILogger<RemoveHttpEndpoint> logger) : DuetAPI.Commands.RemoveHttpEndpoint
{
    /// <summary>
    /// Remove a third-party HTTP endpoint
    /// </summary>
    /// <returns>True if the endpoint could be removed</returns>
    public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            for (int i = 0; i < model.SBC!.DSF.HttpEndpoints.Count; i++)
            {
                HttpEndpoint ep = model.SBC!.DSF.HttpEndpoints[i];
                if (ep.EndpointType == EndpointType && ep.Namespace == Namespace && ep.Path == Path)
                {
                    logger.LogDebug("Removed HTTP endpoint {EndpointType} machine/{1}/{2}", EndpointType, Namespace, Path);
                    model.SBC!.DSF.HttpEndpoints.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }
}
