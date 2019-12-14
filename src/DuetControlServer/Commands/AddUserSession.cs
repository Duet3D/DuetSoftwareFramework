using DuetAPI.Machine;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.AddUserSession"/> command
    /// </summary>
    public class AddUserSession : DuetAPI.Commands.AddUserSession
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
                // Don't create duplicates, reuse existing items if possible...
                foreach (UserSession userSession in Model.Provider.Get.UserSessions)
                {
                    if (userSession.AccessLevel == AccessLevel &&
                        userSession.Origin == Origin && userSession.OriginId == OriginPort &&
                        userSession.SessionType == SessionType)
                    {
                        return userSession.Id;
                    }
                }

                // Create a new session
                UserSession newSession = new UserSession();
                Model.Provider.Get.UserSessions.Add(newSession);

                newSession.AccessLevel = AccessLevel;
                newSession.Id = _idCounter++;
                newSession.Origin = Origin;
                newSession.OriginId = OriginPort;
                newSession.SessionType = SessionType;

                return newSession.Id;
            }
        }
    }
}
