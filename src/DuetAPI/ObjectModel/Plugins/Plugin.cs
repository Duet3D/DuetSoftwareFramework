namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing a loaded plugin
    /// </summary>
    public sealed class Plugin : PluginManifest
    {
        /// <summary>
        /// List of files for the DSF plugin
        /// </summary>
        public ModelCollection<string> DsfFiles { get; } = [];

        /// <summary>
        /// List of files for the DWC plugin
        /// </summary>
        public ModelCollection<string> DwcFiles { get; } = [];

        /// <summary>
        /// List of files to be installed to the (virtual) SD excluding web files
        /// </summary>
        public ModelCollection<string> SdFiles { get; } = [];

        /// <summary>
        /// Process ID of the plugin or -1 if not started. It is set to 0 while the plugin is being shut down
        /// </summary>
        public int Pid
        {
            get => _pid;
            set => SetPropertyValue(ref _pid, value);
        }
        private int _pid = -1;
    }
}
