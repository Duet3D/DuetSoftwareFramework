using DuetAPI.Connection;
using DuetAPI.Utility;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a third-party plugin
    /// </summary>
    public class PluginManifest : ModelObject
    {
		/// <summary>
		/// Name of the plugin
		/// </summary>
		public string Name { get; set; }

		/// <summary>
		/// Author of the plugin
		/// </summary>
		public string Author { get; set; }

		/// <summary>
		/// Version of the plugin
		/// </summary>
		public string Version { get; set; } = "1.0.0";

		/// <summary>
		/// License of the plugin
		/// </summary>
		public string License { get; set; } = "LGPL-3.0";

		/// <summary>
		/// Link to the source code repository
		/// </summary>
		public string SourceRepository { get; set; }

		/// <summary>
		/// Major/minor compatible DWC version
		/// </summary>
		public string DwcVersion { get; set; }

		/// <summary>
		/// List of DWC plugins this plugin depends on. Circular dependencies are not supported
		/// </summary>
		public ModelCollection<string> DwcDependencies { get; } = new ModelCollection<string>();

		/// <summary>
		/// List of CSS and JS files to load in DWC
		/// </summary>
		public ModelCollection<string> DwcResources { get; } = new ModelCollection<string>();

		/// <summary>
		/// Set to true if a SBC is absolutely required for this plugin
		/// </summary>
		public bool SbcRequired { get; set; }

		/// <summary>
		/// DSF API version of the plugin running on the SBC (ignored if there is no SBC executable)
		/// </summary>
		public int SbcApiVersion { get; set; }

		/// <summary>
		/// Filename in the bin directory used to start the plugin
		/// </summary>
		public string SbcExecutable { get; set; }

		/// <summary>
		/// Command-line arguments for the executable
		/// </summary>
		public string SbcExecutableArguments { get; set; }

		/// <summary>
		/// List of permissions required by the plugin executable running on the SBC
		/// </summary>
		public SbcPermissions SbcPermissions { get; set; }

		/// <summary>
		/// List of SBC plugins this plugin depends on. Circular dependencies are not supported
		/// </summary>
		public ModelCollection<string> SbcDependencies { get; } = new ModelCollection<string>();

		/// <summary>
		/// Major/minor supported RRF version (optional)
		/// </summary>
		public string RrfVersion { get; set; }

		/// <summary>
		/// List of RRF files on the (virtual) SD excluding web files
		/// </summary>
		public ModelCollection<string> RrfFiles { get; } = new ModelCollection<string>();

		/// <summary>
		/// List of installed SBC Files in the plugin directory
		/// </summary>
		public ModelCollection<string> SbcFiles { get; } = new ModelCollection<string>();
	}
}
