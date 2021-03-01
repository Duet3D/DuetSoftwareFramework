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
        /// Name of the plugin
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
        private string _name;

        /// <summary>
        /// Author of the plugin
        /// </summary>
        public string Author
        {
            get => _author;
            set => SetPropertyValue(ref _author, value);
        }
        private string _author;

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
        public string Homepage
        {
            get => _homepage;
            set => SetPropertyValue(ref _homepage, value);
        }
        private string _homepage;

        /// <summary>
        /// Major/minor compatible DWC version
        /// </summary>
        public string DwcVersion
        {
            get => _dwcVersion;
            set => SetPropertyValue(ref _dwcVersion, value);
        }
        private string _dwcVersion;

        /// <summary>
        /// List of DWC plugins this plugin depends on. Circular dependencies are not supported
        /// </summary>
        public ModelCollection<string> DwcDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// Name of the generated webpack chunk
        /// </summary>
        public string DwcWebpackChunk
        {
            get => _dwcWebpackChunk;
            set => SetPropertyValue(ref _dwcWebpackChunk, value);
        }
        private string _dwcWebpackChunk;

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
        public string SbcDsfVersion
        {
            get => _sbcDsfVersion;
            set => SetPropertyValue(ref _sbcDsfVersion, value);
        }
        private string _sbcDsfVersion;

        /// <summary>
        /// Object holding key value pairs of a plugin running on the SBC.
        /// May be used to share data between plugins or between the SBC and web interface
        /// </summary>
        public ModelDictionary<JsonElement> SbcData { get; } = new ModelDictionary<JsonElement>();

        /// <summary>
        /// Filename in the bin directory used to start the plugin
        /// </summary>
        /// <remarks>
        /// A plugin may provide different binaries in subdirectories per architecture.
        /// Supported architectures are: arm, arm64, x86, x86_64
        /// </remarks>
        public string SbcExecutable
        {
            get => _sbcExecutable;
            set
            {
                if (value.Contains(".."))
                {
                    throw new ArgumentException("Executable must not contain relative file paths");
                }
                SetPropertyValue(ref _sbcExecutable, value);
            }
        }
        private string _sbcExecutable;

        /// <summary>
        /// Command-line arguments for the executable
        /// </summary>
        public string SbcExecutableArguments
        {
            get => _sbcExecutableArguments;
            set => SetPropertyValue(ref _sbcExecutableArguments, value);
        }
        private string _sbcExecutableArguments;

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
        /// List of SBC plugins this plugin depends on. Circular dependencies are not supported
        /// </summary>
        public ModelCollection<string> SbcPluginDependencies { get; } = new ModelCollection<string>();

        /// <summary>
        /// Major/minor supported RRF version (optional)
        /// </summary>
        public string RrfVersion
        {
            get => _rrfVersion;
            set => SetPropertyValue(ref _rrfVersion, value);
        }
        private string _rrfVersion;

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
