using DuetAPI.ObjectModel;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.AddUserSession"/> command
    /// </summary>
    public sealed class AddUserSession : DuetAPI.Commands.AddUserSession
    {
        /// <summary>
        /// Counter for new user sessions
        /// </summary>
        private static int _idCounter = 1;

        /// <summary>
        /// Add a new user session
        /// </summary>
        /// <returns>Session ID</returns>
        public override async Task<int> Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                UserSession newSession = new()
                {
                    AccessLevel = AccessLevel,
                    Id = _idCounter++,
                    Origin = Origin,
                    SessionType = SessionType
                };
                Model.Provider.Get.SBC!.DSF.UserSessions.Add(newSession);

                return newSession.Id;
            }
        }
    }
}
