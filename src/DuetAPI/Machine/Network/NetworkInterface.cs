using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a network interface
    /// </summary>
    public sealed class NetworkInterface : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Type of this network interface
        /// </summary>
        public InterfaceType Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private InterfaceType _type = InterfaceType.WiFi;
        
        /// <summary>
        /// Version of the network interface or null if unknown.
        /// This is primarily intended for the ESP8266-based network interfaces as used on the Duet WiFi
        /// </summary>
        public string FirmwareVersion
        {
            get => _firmwareVersion;
            set
            {
                if (_firmwareVersion != value)
                {
                    _firmwareVersion = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _firmwareVersion;
        
        /// <summary>
        /// Speed of the network interface (in MBit, null if unknown, 0 if not connected)
        /// </summary>
        public int? Speed
        {
            get => _speed;
            set
            {
                if (_speed != value)
                {
                    _speed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _speed;
        
        /// <summary>
        /// Signal of the WiFi adapter (only WiFi, in dBm)
        /// </summary>
        public int? Signal
        {
            get => _signal;
            set
            {
                if (_signal != value)
                {
                    _signal = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _signal;

        /// <summary>
        /// Physical address of the network adapter
        /// </summary>
        public string MacAddress
        {
            get => _macAddress;
            set
            {
                if (_macAddress != value)
                {
                    _macAddress = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _macAddress;
        
        /// <summary>
        /// Configured IPv4 address of the network adapter
        /// </summary>
        public string ConfiguredIP
        {
            get => _configuredIP;
            set
            {
                if (_configuredIP != value)
                {
                    _configuredIP = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _configuredIP;
        
        /// <summary>
        /// Actual IPv4 address of the network adapter
        /// </summary>
        public string ActualIP
        {
            get => _actualIP;
            set
            {
                if (_actualIP != value)
                {
                    _actualIP = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _actualIP;
        
        /// <summary>
        /// Subnet of the network adapter
        /// </summary>
        public string Subnet
        {
            get => _subnet;
            set
            {
                if (_subnet != value)
                {
                    _subnet = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _subnet;

        /// <summary>
        /// Gateway of the network adapter
        /// </summary>
        public string Gateway
        {
            get => _gateway;
            set
            {
                if (_gateway != value)
                {
                    _gateway = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _gateway;
        
        /// <summary>
        /// Number of reconnect attempts or null if unknown
        /// </summary>
        public int? NumReconnects
        {
            get => _numReconnects;
            set
            {
                if (_numReconnects != value)
                {
                    _numReconnects = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _numReconnects;

        /// <summary>
        /// List of active protocols
        /// </summary>
        public ObservableCollection<NetworkProtocol> ActiveProtocols { get; } = new ObservableCollection<NetworkProtocol>();

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
            if (!(from is NetworkInterface other))
            {
                throw new ArgumentException("Invalid type");
            }

            Type = other.Type;
            FirmwareVersion = (other.FirmwareVersion != null) ? string.Copy(other.FirmwareVersion) : null;
            Speed = other.Speed;
            Signal = other.Signal;
            MacAddress = (other.MacAddress != null) ? string.Copy(other.MacAddress) : null;
            ConfiguredIP = (other.ConfiguredIP != null) ? string.Copy(other.ConfiguredIP) : null;
            ActualIP = (other.ActualIP != null) ? string.Copy(other.ActualIP) : null;
            Subnet = (other.Subnet != null) ? string.Copy(other.Subnet) : null;
            Gateway = (other.Gateway != null) ? string.Copy(other.Gateway) : null;
            NumReconnects = other.NumReconnects;
            ListHelpers.SetList(ActiveProtocols, other.ActiveProtocols);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            NetworkInterface clone = new NetworkInterface
            {
                Type = Type,
                FirmwareVersion = (FirmwareVersion != null) ? string.Copy(FirmwareVersion) : null,
                Speed = Speed,
                Signal = Signal,
                MacAddress = (MacAddress != null) ? string.Copy(MacAddress) : null,
                ConfiguredIP = (ConfiguredIP != null) ? string.Copy(ConfiguredIP) : null,
                ActualIP = (ActualIP != null) ? string.Copy(ActualIP) : null,
                Subnet = (Subnet != null) ? string.Copy(Subnet) : null,
                Gateway = (Gateway != null) ? string.Copy(Gateway) : null,
                NumReconnects = NumReconnects
            };

            ListHelpers.AddItems(clone.ActiveProtocols, ActiveProtocols);

            return clone;
        }
    }
}
