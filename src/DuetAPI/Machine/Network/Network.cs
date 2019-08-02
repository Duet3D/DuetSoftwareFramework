using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the network subsystem
    /// </summary>
    public sealed class Network : IAssignable, ICloneable, INotifyPropertyChanged
    {
        /// <summary>
        /// Default name of the machine
        /// </summary>
        public const string DefaultName = "My Duet";

        /// <summary>
        /// Fallback hostname if the <c>Name</c> is invalid
        /// </summary>
        public const string DefaultHostname = "duet";

        /// <summary>
        /// Default network password of the machine
        /// </summary>
        public const string DefaultPassword = "reprap";

        /// <summary>
        /// Event to trigger when a property has changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Name of the machine
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _name = DefaultName;

        /// <summary>
        /// Hostname of the machine
        /// </summary>
        public string Hostname
        {
            get
            {
                string hostname = "";
                foreach (char c in Name)
                {
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    {
                        hostname += c;
                    }
                }
                return (hostname != "") ? hostname : DefaultHostname;
            }
        }
        
        /// <summary>
        /// Password required to access this machine
        /// </summary>
        /// <remarks>
        /// This concept is deprecated and may be dropped in favour of user authentication in the future
        /// </remarks>
        public string Password
        {
            get => _password;
            set
            {
                if (_password != value)
                {
                    _password = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _password = DefaultPassword;

        /// <summary>
        /// List of available network interfaces
        /// </summary>
        /// <seealso cref="NetworkInterface"/>
        public ObservableCollection<NetworkInterface> Interfaces { get; } = new ObservableCollection<NetworkInterface>();

        /// <summary>
        /// Assigns every property of another instance of this one
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
            if (!(from is Network other))
            {
                throw new ArgumentException("Invalid type");
            }

            Name = (other.Name != null) ? string.Copy(other.Name) : null;
            Password = (other.Password != null) ? string.Copy(other.Password) : null;
            ListHelpers.AssignList(Interfaces, other.Interfaces);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Network clone = new Network
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Password = (Password != null) ? string.Copy(Password) : null
            };

            ListHelpers.CloneItems(clone.Interfaces, Interfaces);

            return clone;
        }
    }
}
