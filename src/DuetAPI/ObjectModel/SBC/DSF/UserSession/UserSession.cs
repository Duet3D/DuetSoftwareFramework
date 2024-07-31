namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing a user session
    /// </summary>
    public partial class UserSession : ModelObject
    {
        /// <summary>
        /// Access level of this session
        /// </summary>
        public AccessLevel AccessLevel
        {
            get => _accessLevel;
			set => SetPropertyValue(ref _accessLevel, value);
        }
        private AccessLevel _accessLevel;

        /// <summary>
        /// Identifier of this session
        /// </summary>
        public int Id
        {
            get => _id;
			set => SetPropertyValue(ref _id, value);
        }
        private int _id;

        /// <summary>
        /// Origin of this session. For remote sessions, this equals the remote IP address
        /// </summary>
        public string Origin
        {
            get => _origin;
			set => SetPropertyValue(ref _origin, value);
        }
        private string _origin = string.Empty;

        /// <summary>
        /// Corresponding identifier of the origin.
        /// If it is a remote session, it is the remote port, else it defaults to the PID of the current process
        /// </summary>
        public int OriginId
        {
            get => _originId;
			set => SetPropertyValue(ref _originId, value);
        }
        private int _originId = -1;

        /// <summary>
        /// Type of this session
        /// </summary>
        public SessionType SessionType
        {
            get => _sessionType;
			set => SetPropertyValue(ref _sessionType, value);
        }
        private SessionType _sessionType = SessionType.Local;
    }
}
