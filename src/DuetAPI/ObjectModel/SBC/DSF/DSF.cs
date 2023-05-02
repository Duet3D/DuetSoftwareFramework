using System.Reflection;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about Duet Software Framework
    /// </summary>
    public class DSF : ModelObject
    {
        /// <summary>
        /// List of registered third-party HTTP endpoints
        /// </summary>
        public ModelCollection<HttpEndpoint> HttpEndpoints { get; } = new ModelCollection<HttpEndpoint>();

        /// <summary>
        /// Indicates if DSF allows the installation and usage of third-party plugins
        /// </summary>
        [SbcProperty(false)]
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
        [SbcProperty(false)]
        public bool RootPluginSupport
        {
            get => _rootPluginSupport;
            set => SetPropertyValue(ref _rootPluginSupport, value);
        }
        private bool _rootPluginSupport;

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
