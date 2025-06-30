using DuetAPI.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.AddUserSession"/> command
/// </summary>
/// <param name="model">Object model</param>
public sealed class AddUserSession(Model.ObjectModel model) : DuetAPI.Commands.AddUserSession
{
    /// <summary>
    /// Counter for new user sessions
    /// </summary>
    private static int _idCounter = 1;

    /// <summary>
    /// Add a new user session
    /// </summary>
    /// <returns>Session ID</returns>
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            UserSession newSession = new()
            {
                AccessLevel = AccessLevel,
                Id = _idCounter++,
                Origin = Origin,
                SessionType = SessionType
            };
            model.SBC!.DSF.UserSessions.Add(newSession);

            return newSession.Id;
        }
    }
}
