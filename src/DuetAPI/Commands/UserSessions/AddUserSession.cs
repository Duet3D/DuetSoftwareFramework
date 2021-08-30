using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Register a new user session.
    /// Returns the ID of the new user session
    /// </summary>
    [RequiredPermissions(SbcPermissions.ManageUserSessions)]
    public class AddUserSession : Command<int>
    {
        /// <summary>
        /// Access level of this session
        /// </summary>
        public AccessLevel AccessLevel { get; set; }

        /// <summary>
        /// Type of this session
        /// </summary>
        public SessionType SessionType { get; set; }

        /// <summary>
        /// Origin of this session. For remote sessions, this equals the remote IP address
        /// </summary>
        public string Origin { get; set; }
    }
}
