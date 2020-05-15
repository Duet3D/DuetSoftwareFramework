namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a network interface
    /// </summary>
    public sealed class NetworkInterface : ModelObject
    {
        /// <summary>
        /// List of active protocols
        /// </summary>
        public ModelCollection<NetworkProtocol> ActiveProtocols { get; } = new ModelCollection<NetworkProtocol>();

        /// <summary>
        /// Actual IPv4 address of the network adapter
        /// </summary>
        public string ActualIP
        {
            get => _actualIP;
			set => SetPropertyValue(ref _actualIP, value);
        }
        private string _actualIP;

        /// <summary>
        /// Configured IPv4 address of the network adapter
        /// </summary>
        public string ConfiguredIP
        {
            get => _configuredIP;
			set => SetPropertyValue(ref _configuredIP, value);
        }
        private string _configuredIP;

        /// <summary>
        /// Version of the network interface or null if unknown.
        /// This is primarily intended for the ESP8266-based network interfaces as used on the Duet WiFi
        /// </summary>
        public string FirmwareVersion
        {
            get => _firmwareVersion;
			set => SetPropertyValue(ref _firmwareVersion, value);
        }
        private string _firmwareVersion;

        /// <summary>
        /// Gateway of the network adapter
        /// </summary>
        public string Gateway
        {
            get => _gateway;
			set => SetPropertyValue(ref _gateway, value);
        }
        private string _gateway;

        /// <summary>
        /// Physical address of the network adapter
        /// </summary>
        public string Mac
        {
            get => _mac;
			set => SetPropertyValue(ref _mac, value);
        }
        private string _mac;

        /// <summary>
        /// Number of reconnect attempts or null if unknown
        /// </summary>
        public int? NumReconnects
        {
            get => _numReconnects;
			set => SetPropertyValue(ref _numReconnects, value);
        }
        private int? _numReconnects;

        /// <summary>
        /// Signal of the WiFi adapter (only WiFi, in dBm, or null if unknown)
        /// </summary>
        public int? Signal
        {
            get => _signal;
			set => SetPropertyValue(ref _signal, value);
        }
        private int? _signal;

        /// <summary>
        /// Speed of the network interface (in MBit, null if unknown, 0 if not connected)
        /// </summary>
        public int? Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private int? _speed;

        /// <summary>
        /// Subnet of the network adapter
        /// </summary>
        public string Subnet
        {
            get => _subnet;
			set => SetPropertyValue(ref _subnet, value);
        }
        private string _subnet;

        /// <summary>
        /// Type of this network interface
        /// </summary>
        public InterfaceType Type
        {
            get => _type;
			set => SetPropertyValue(ref _type, value);
        }
        private InterfaceType _type = InterfaceType.WiFi;
    }
}
