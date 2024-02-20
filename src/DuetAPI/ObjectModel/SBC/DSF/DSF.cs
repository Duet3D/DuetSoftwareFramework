using System.Reflection;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about Duet Software Framework
    /// </summary>
    public class DSF : ModelObject
    {
        /// <summary>
        /// Datetime when DSF was built
        /// </summary>
        public string BuildDateTime
        {
            get => _buildDateTime;
            set => SetPropertyValue(ref _buildDateTime, value);
        }
        private string _buildDateTime = string.Empty;

        /// <summary>
        /// List of registered third-party HTTP endpoints
        /// </summary>
        public ModelCollection<HttpEndpoint> HttpEndpoints { get; } = new ModelCollection<HttpEndpoint>();

        /// <summary>
        /// Indicates if the process is 64-bit
        /// </summary>
        public bool Is64Bit
        {
            get => _is64Bit;
            set => SetPropertyValue(ref _is64Bit, value);
        }
        private bool _is64Bit;

        /// <summary>
        /// Indicates if DSF allows the installation and usage of third-party plugins
        /// </summary>
        public bool PluginSupport
        {
            get => _pluginSupport;
            set => SetPropertyValue(ref _pluginSupport, value);
        }
        private bool _pluginSupport;

        /// <summary>
        /// Indicates if DSF allows the installation and usage of third-party root plugins (potentially dangerous, disabled by default)
        /// </summary>
        /// <remarks>
        /// Requires <see cref="PluginSupport"/> to be true
        /// </remarks>
        public bool RootPluginSupport
        {
            get => _rootPluginSupport;
            set => SetPropertyValue(ref _rootPluginSupport, value);
        }
        private bool _rootPluginSupport;

        /// <summary>
        /// List of user sessions
        /// </summary>
        public ModelCollection<UserSession> UserSessions { get; } = new ModelCollection<UserSession>();

        /// <summary>
        /// Version of Duet Software Framework (provided by Duet Control Server)
        /// </summary>
        public string Version
        {
            get => _version;
            set => SetPropertyValue(ref _version, value);
        }
        private string _version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    }
}
