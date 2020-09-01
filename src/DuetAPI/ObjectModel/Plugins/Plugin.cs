namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class representing a loaded plugin
    /// </summary>
    public sealed class Plugin : PluginManifest
    {
		/// <summary>
		/// List of files for DWC
		/// </summary>
		public ModelCollection<string> DwcFiles { get; } = new ModelCollection<string>();

		/// <summary>
		/// List of installed SBC Files in the plugin directory
		/// </summary>
		public ModelCollection<string> SbcFiles { get; } = new ModelCollection<string>();

		/// <summary>
		/// List of RRF files on the (virtual) SD excluding web files
		/// </summary>
		public ModelCollection<string> RrfFiles { get; } = new ModelCollection<string>();

        /// <summary>
        /// Process ID if the plugin or -1 if not started
        /// </summary>
		/// <remarks>
		/// This may become 0 when the plugin has been stopped and the application is being shut down
		/// </remarks>
		public int Pid
		{
			get => _pid;
			set => SetPropertyValue(ref _pid, value);
		}
		private int _pid = -1;
	}
}
