using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Class representing an extra HTTP endpoint
    /// </summary>
    /// <seealso cref="Commands.AddHttpEndpoint"/>
    /// <seealso cref="Commands.RemoveHttpEndpoint"/>
    public class HttpEndpoint : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// HTTP type of this endpoint
        /// </summary>
        public HttpEndpointType EndpointType
        {
            get => _endpointType;
            set
            {
                if (_endpointType != value)
                {
                    _endpointType = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private HttpEndpointType _endpointType;

        /// <summary>
        /// Namespace of the endpoint
        /// </summary>
        public string Namespace
        {
            get => _namespace;
            set
            {
                if (_namespace != value)
                {
                    _namespace = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _namespace;

        /// <summary>
        /// Path to the endpoint
        /// </summary>
        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _path;

        /// <summary>
        /// Path to the UNIX socket
        /// </summary>
        public string UnixSocket
        {
            get => _unixSocket;
            set
            {
                if (_unixSocket != value)
                {
                    _unixSocket = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _unixSocket;

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
            if (!(from is HttpEndpoint other))
            {
                throw new ArgumentException("Invalid type");
            }

            EndpointType = other.EndpointType;
            Namespace = other.Namespace;
            Path = other.Path;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            HttpEndpoint clone = new HttpEndpoint
            {
                EndpointType = EndpointType,
                Namespace = Namespace,
                Path = Path,
                UnixSocket = UnixSocket
            };

            return clone;
        }
    }
}
