using DuetAPI.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.Command"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class GetObjectModel(Model.ObjectModel model) : DuetAPI.Commands.GetObjectModel
{
    /// <summary>
    /// Retrieve a copy of the current machine model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Clone of the current machine model</returns>
    public override async Task<ObjectModel> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return (ObjectModel)model.Clone();
        }
    }
}
