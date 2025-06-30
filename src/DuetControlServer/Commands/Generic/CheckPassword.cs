using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.CheckPassword"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class CheckPassword(Model.ObjectModel model) : DuetAPI.Commands.CheckPassword
{
    /// <summary>
    /// Check the given password
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Password == DuetAPI.Connection.Defaults.Password || string.IsNullOrEmpty(model.Password))
            {
                // No password set
                return true;
            }
            return Password == model.Password;
        }
    }
}
