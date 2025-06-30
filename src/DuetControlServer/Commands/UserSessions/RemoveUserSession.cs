using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.RemoveUserSession"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class RemoveUserSession(Model.ObjectModel model) : DuetAPI.Commands.RemoveUserSession
{
    /// <summary>
    /// Remove an existing user session
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if the user session could be removed</returns>
    public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            for (int i = 0; i < model.SBC!.DSF.UserSessions.Count; i++)
            {
                if (model.SBC!.DSF.UserSessions[i].Id == Id)
                {
                    model.SBC!.DSF.UserSessions.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }
}
