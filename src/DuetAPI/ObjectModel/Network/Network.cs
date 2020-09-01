namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the network subsystem
    /// </summary>
    public sealed class Network : ModelObject
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
        /// Hostname of the machine
        /// </summary>
        public string Hostname
        {
            get => _hostname;
			set => SetPropertyValue(ref _hostname, value);
        }
        private string _hostname = DefaultHostname;

        /// <summary>
        /// List of available network interfaces
        /// </summary>
        /// <seealso cref="NetworkInterface"/>
        [LinuxProperty]
        public ModelCollection<NetworkInterface> Interfaces { get; } = new ModelCollection<NetworkInterface>();

        /// <summary>
        /// Name of the machine
        /// </summary>
        public string Name
        {
            get => _name;
			set => SetPropertyValue(ref _name, value);
        }
        private string _name = DefaultName;
    }
}
