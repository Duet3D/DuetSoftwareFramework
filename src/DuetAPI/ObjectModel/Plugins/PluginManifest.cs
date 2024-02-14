using DuetAPI.Utility;
using System;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a third-party plugin
    /// </summary>
    public class PluginManifest : ModelObject
    {
        /// <summary>
        /// Identifier of this plugin. May consist of letters and digits only (max length 32 chars)
        /// </summary>
        /// <remarks>
        /// For plugins with DWC components, this is the Webpack chunk name too
        /// </remarks>
        public string Id
        {
            get => _id;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 32)
                {
                    throw new ArgumentException("Invalid plugin identifier");
                }

                foreach (char c in value)
                {
                    if (!char.IsLetterOrDigit(c))
                    {
                        throw new ArgumentException("Illegal plugin identifier");
                    }
                }

                SetPropertyValue(ref _id, value);
            }
        }
        private string _id = string.Empty;

        /// <summary>
        /// Name of the plugin. May consist of letters, digits, dashes, and underscores only (max length 64 chars)
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
                {
                    throw new ArgumentException("Invalid plugin name");
                }

                foreach (char c in value)
                {
                    if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_')
                    {
                        throw new ArgumentException("Illegal plugin name");
                    }
                }

                SetPropertyValue(ref _name, value);
            }
        }
        private string _name = string.Empty;

        /// <summary>
        /// Author of the plugin
        /// </summary>
        public string Author
        {
            get => _author;
            set => SetPropertyValue(ref _author, value);
        }
        private string _author = string.Empty;

        /// <summary>
        /// Version of the plugin
        /// </summary>
        public string Version
        {
            get => _version;
            set => SetPropertyValue(ref _version, value);
        }
        private string _version = "1.0.0";

        /// <summary>
        /// License of the plugin. Should follow the SPDX format (see https://spdx.org/licenses/)
        /// </summary>
        public string License
        {
            get => _license;
            set => SetPropertyValue(ref _license, value);
        }
        private string _license = "LGPL-3.0-or-later";

        /// <summary>
        /// Link to the plugin homepage or source code repository
        /// </summary>
        public string? Homepage
        {
            get => _homepage;
            set => SetPropertyValue(ref _homepage, value);
        }
        private string? _homepage;

        /// <summary>
        /// List of general tags for search
        /// </summary>
        public ModelCollection<string> Tags { get; } = new ModelCollection<string>();

        /// <summary>
        /// Major/minor compatible DWC version
        /// </summary>
        public string? DwcVersion
        {
            get => _dwcVersion;
            set => SetPropertyValue(ref _dwcVersion, value);
        }
        private string? _dwcVersion;

        /// <summary>
        /// List of DWC plugins this plugin depends on. Circular dependencies are not supported
        /// </summary>
        public ModelCollection<string> DwcDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// Set to true if a SBC is absolutely required for this plugin
        /// </summary>
        public bool SbcRequired
        {
            get => _sbcRequired;
            set => SetPropertyValue(ref _sbcRequired, value);
        }
        private bool _sbcRequired;

        /// <summary>
        /// Required DSF version for the plugin running on the SBC (ignored if there is no SBC executable)
        /// </summary>
        public string? SbcDsfVersion
        {
            get => _sbcDsfVersion;
            set => SetPropertyValue(ref _sbcDsfVersion, value);
        }
        private string? _sbcDsfVersion;

        /// <summary>
        /// Filename in the dsf directory used to start the plugin
        /// </summary>
        /// <remarks>
        /// A plugin may provide different binaries in subdirectories per architecture.
        /// Supported architectures are: arm, arm64, x86, x86_64
        /// </remarks>
        public string? SbcExecutable
        {
            get => _sbcExecutable;
            set
            {
                if (value is not null && value.Contains(".."))
                {
                    throw new ArgumentException("Executable must not contain relative file paths");
                }
                SetPropertyValue(ref _sbcExecutable, value);
            }
        }
        private string? _sbcExecutable;

        /// <summary>
        /// Command-line arguments for the executable
        /// </summary>
        public string? SbcExecutableArguments
        {
            get => _sbcExecutableArguments;
            set => SetPropertyValue(ref _sbcExecutableArguments, value);
        }
        private string? _sbcExecutableArguments;

        /// <summary>
        /// List of other filenames in the dsf directory that should be executable
        /// </summary>
        public ModelCollection<string> SbcExtraExecutables { get; } = new ModelCollection<string>();

        /// <summary>
        /// Automatically restart the SBC process when terminated
        /// </summary>
        public bool SbcAutoRestart
        {
            get => _sbcAutoRestart;
            set => SetPropertyValue(ref _sbcAutoRestart, value);
        }
        private bool _sbcAutoRestart;

        /// <summary>
        /// List of files in the sys or virtual SD directory that should not be overwritten on upgrade
        /// </summary>
        /// <remarks>
        /// The file may be specified either relative to 0:/sys directory (e.g. motion.conf) or relative to the
        /// virtual SD directory (e.g. sys/motion.conf). Drive indices as in 0:/sys/motion.conf are not allowed!
        /// </remarks>
        public ModelCollection<string> SbcConfigFiles { get; } = new ModelCollection<string>();

        /// <summary>
        /// Defines if messages from stdout/stderr are output as generic messages
        /// </summary>
        public bool SbcOutputRedirected
        {
            get => _sbcOutputRedirected;
            set => SetPropertyValue(ref _sbcOutputRedirected, value);
        }
        private bool _sbcOutputRedirected = true;

        /// <summary>
        /// List of permissions required by the plugin executable running on the SBC
        /// </summary>
        public SbcPermissions SbcPermissions
        {
            get => _sbcPermissions;
            set => SetPropertyValue(ref _sbcPermissions, value);
        }
        private SbcPermissions _sbcPermissions;

        /// <summary>
        /// List of packages this plugin depends on (apt packages in the case of DuetPi)
        /// </summary>
        public ModelCollection<string> SbcPackageDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// List of Python packages this plugin depends on
        /// </summary>
        public ModelCollection<string> SbcPythonDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// List of SBC plugins this plugin depends on. Circular dependencies are not supported
        /// </summary>
        public ModelCollection<string> SbcPluginDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// Major/minor supported RRF version (optional)
        /// </summary>
        public string? RrfVersion
        {
            get => _rrfVersion;
            set => SetPropertyValue(ref _rrfVersion, value);
        }
        private string? _rrfVersion;

        /// <summary>
        /// Custom plugin data to be populated in the object model (DSF/DWC in SBC mode - or - DWC in standalone mode).
        /// Before <see cref="Commands.SetPluginData"/> can be used, corresponding properties must be registered via this property first!
        /// </summary>
        /// <seealso cref="Commands.SetPluginData"/>
        public ModelDictionary<JsonElement> Data { get; } = new ModelDictionary<JsonElement>(false);

        /// <summary>
        /// Check if the given version satisfies a required version
        /// </summary>
        /// <param name="actual">Actual version</param>
        /// <param name="required">Required version</param>
        /// <returns>Whether the actual version fulfills teh requirement</returns>
        public static bool CheckVersion(string actual, string required)
        {
            if (!string.IsNullOrWhiteSpace(required))
            {
                string[] actualItems = actual.Split(new char[] { '.', '-', '+' });
                string[] requiredItems = required.Split(new char[] { '.', '-', '+' });
                for (int i = 0; i < Math.Min(actualItems.Length, requiredItems.Length); i++)
                {
                    if (actualItems[i] != requiredItems[i])
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
