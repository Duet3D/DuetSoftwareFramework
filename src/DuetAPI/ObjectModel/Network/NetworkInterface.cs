namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a network interface
    /// </summary>
    public sealed class NetworkInterface : ModelObject
    {
        /// <summary>
        /// List of active protocols
        /// </summary>
        [SbcProperty(false)]
        public ModelCollection<NetworkProtocol> ActiveProtocols { get; } = [];

        /// <summary>
        /// Actual IPv4 address of the network adapter or null if unknown
        /// </summary>
        public string? ActualIP
        {
            get => _actualIP;
			set => SetPropertyValue(ref _actualIP, value);
        }
        private string? _actualIP;

        /// <summary>
        /// Configured IPv4 address of the network adapter or null if unknown
        /// </summary>
        [SbcProperty(false)]
        public string? ConfiguredIP
        {
            get => _configuredIP;
			set => SetPropertyValue(ref _configuredIP, value);
        }
        private string? _configuredIP;

        /// <summary>
        /// Configured IPv4 DNS server of the network adapter or null if unknown
        /// </summary>
        [SbcProperty(false)]
        public string? DnsServer
        {
            get => _dnsServer;
            set => SetPropertyValue(ref _dnsServer, value);
        }
        private string? _dnsServer;

        /// <summary>
        /// Version of the network interface or null if unknown.
        /// This is only reported by ESP-based boards in standalone mode
        /// </summary>
        public string? FirmwareVersion
        {
            get => _firmwareVersion;
			set => SetPropertyValue(ref _firmwareVersion, value);
        }
        private string? _firmwareVersion;

        /// <summary>
        /// IPv4 gateway of the network adapter or null if unknown
        /// </summary>
        public string? Gateway
        {
            get => _gateway;
			set => SetPropertyValue(ref _gateway, value);
        }
        private string? _gateway;

        /// <summary>
        /// Physical address of the network adapter or null if unknown
        /// </summary>
        public string? Mac
        {
            get => _mac;
			set => SetPropertyValue(ref _mac, value);
        }
        private string? _mac;

        /// <summary>
        /// Number of reconnect attempts or null if unknown.
        /// This is only reported by ESP-based boards in standalone mode
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
        [SbcProperty(false)]
        public int? Signal
        {
            get => _signal;
			set => SetPropertyValue(ref _signal, value);
        }
        private int? _signal;

        /// <summary>
        /// Speed of the network interface (in MBit, null if unknown, 0 if not connected)
        /// </summary>
        [SbcProperty(false)]
        public int? Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private int? _speed;

        /// <summary>
        /// SSID of the WiFi network or null if not applicable
        /// </summary>
        [SbcProperty(true)]
        public string? SSID
        {
            get => _ssid;
            set => SetPropertyValue(ref _ssid, value);
        }
        private string? _ssid = null;

        /// <summary>
        /// State of this network interface or null if unknown
        /// </summary>
        public NetworkState? State
        {
            get => _state;
            set => SetPropertyValue(ref _state, value);
        }
        private NetworkState? _state;

        /// <summary>
        /// Subnet of the network adapter or null if unknown
        /// </summary>
        public string? Subnet
        {
            get => _subnet;
			set => SetPropertyValue(ref _subnet, value);
        }
        private string? _subnet;

        /// <summary>
        /// Type of this network interface
        /// </summary>
        public NetworkInterfaceType Type
        {
            get => _type;
			set => SetPropertyValue(ref _type, value);
        }
        private NetworkInterfaceType _type = NetworkInterfaceType.WiFi;

        /// <summary>
        /// WiFi country code if this is a WiFi adapter and if the country code can be determined
        /// </summary>
        /// <remarks>
        /// For this setting to be populated in SBC mode it is required to have the DuetPiManagementPlugin running.
        /// This is required due to missing Linux permissions of the control server.
        /// </remarks>
        [SbcProperty(false)]
        public string? WifiCountry
        {
            get => _wifiCountry;
            set => SetPropertyValue(ref _wifiCountry, value);
        }
        private string? _wifiCountry;
    }
}
