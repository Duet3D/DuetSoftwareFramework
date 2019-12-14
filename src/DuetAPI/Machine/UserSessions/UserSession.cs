using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Class representing a user session
    /// </summary>
    public class UserSession : IAssignable, ICloneable, INotifyPropertyChanged
    {
        /// <summary>
        /// Event to trigger when a property has changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Identifier of this session
        /// </summary>
        public int Id
        {
            get => _id;
            set
            {
                if (_id != value)
                {
                    _id = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _id;

        /// <summary>
        /// Access level of this session
        /// </summary>
        public AccessLevel AccessLevel
        {
            get => _accessLevel;
            set
            {
                if (_accessLevel != value)
                {
                    _accessLevel = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private AccessLevel _accessLevel;

        /// <summary>
        /// Type of this sessionSessionAccessLevel
        /// </summary>
        public SessionType SessionType
        {
            get => _sessionType;
            set
            {
                if (_sessionType != value)
                {
                    _sessionType = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private SessionType _sessionType;

        /// <summary>
        /// Origin of this session. For remote sessions, this equals the remote IP address
        /// </summary>
        public string Origin
        {
            get => _origin;
            set
            {
                if (_origin != value)
                {
                    _origin = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _origin;

        /// <summary>
        /// Corresponding identifier of the origin.
        /// If it is a remote session, it is the remote port, else it defaults to the PID of the current process
        /// </summary>
        public int OriginId
        {
            get => _originPort;
            set
            {
                if (_originPort != value)
                {
                    _originPort = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _originPort = -1;

        /// <summary>
        /// Assigns every property from another instance
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void Assign(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is UserSession other))
            {
                throw new ArgumentException("Invalid type");
            }

            Id = other.Id;
            AccessLevel = other.AccessLevel;
            SessionType = other.SessionType;
            Origin = other.Origin;
            OriginId = other.OriginId;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            UserSession clone = new UserSession
            {
                Id = Id,
                AccessLevel = AccessLevel,
                SessionType = SessionType,
                Origin = Origin,
                OriginId = OriginId
            };

            return clone;
        }
    }
}
