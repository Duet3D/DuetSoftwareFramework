using DuetAPI.Utility;
using System.Collections.Generic;
using System.Text.Json;

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
		public int? SbcApiVersion { get; set; }

		/// <summary>
		/// Object holding key value pairs of a plugin running on the SBC.
		/// May be used to share data between plugins or between the SBC and web interface
		/// </summary>
		public Dictionary<string, JsonElement> SbcData { get; set; } = new Dictionary<string, JsonElement>();

		/// <summary>
		/// Filename in the bin directory used to start the plugin
		/// </summary>
		/// <remarks>
		/// A plugin may provide different binaries in subdirectories per architecture.
		/// Supported architectures are: arm, arm64, x86, x86_64
		/// </remarks>
		public string SbcExecutable { get; set; }

		/// <summary>
		/// Command-line arguments for the executable
		/// </summary>
		public string SbcExecutableArguments { get; set; }

		/// <summary>
		/// Defines if messages from stdout/stderr are output as generic messages
		/// </summary>
		public bool SbcOutputRedirected { get; set; } = true;

		/// <summary>
		/// List of permissions required by the plugin executable running on the SBC
		/// </summary>
		public SbcPermissions SbcPermissions { get; set; }

		/// <summary>
		/// List of packages this plugin depends on (apt packages in the case of DuetPi)
		/// </summary>
		public ModelCollection<string> SbcPackageDependencies { get; } = new ModelCollection<string>();

		/// <summary>
		/// List of SBC plugins this plugin depends on. Circular dependencies are not supported
		/// </summary>
		public ModelCollection<string> SbcPluginDependencies { get; } = new ModelCollection<string>();

		/// <summary>
		/// Major/minor supported RRF version (optional)
		/// </summary>
		public string RrfVersion { get; set; }
	}
}
