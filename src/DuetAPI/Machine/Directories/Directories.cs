namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the configured directories
    /// </summary>
    public sealed class Directories : ModelObject
    {
        /// <summary>
        /// Path to the filaments directory
        /// </summary>
        public string Filaments
        {
            get => _filaments;
			set => SetPropertyValue(ref _filaments, value);
        }
        private string _filaments = "0:/filaments";

        /// <summary>
        /// Path to the firmware directory
        /// </summary>
        public string Firmware
        {
            get => _firmware;
			set => SetPropertyValue(ref _firmware, value);
        }
        private string _firmware = "0:/sys";

        /// <summary>
        /// Path to the G-Codes directory
        /// </summary>
        public string GCodes
        {
            get => _gcodes;
			set => SetPropertyValue(ref _gcodes, value);
        }
        private string _gcodes = "0:/gcodes";

        /// <summary>
        /// Path to the macros directory
        /// </summary>
        public string Macros
        {
            get => _macros;
			set => SetPropertyValue(ref _macros, value);
        }
        private string _macros = "0:/macros";

        /// <summary>
        /// Path to the menu directory
        /// </summary>
        /// <remarks>
        /// Intended for 12864 displays but currently unused in DSF. It is only needed for the Duet Maestro + DWC
        /// </remarks>
        public string Menu
        {
            get => _menu;
			set => SetPropertyValue(ref _menu, value);
        }
        private string _menu = "0:/menu";

        /// <summary>
        /// Path to the scans directory
        /// </summary>
        public string Scans
        {
            get => _scans;
			set => SetPropertyValue(ref _scans, value);
        }
        private string _scans = "0:/scans";

        /// <summary>
        /// Path to the system directory
        /// </summary>
        public string System
        {
            get => _system;
			set => SetPropertyValue(ref _system, value);
        }
        private string _system = "0:/sys";

        /// <summary>
        /// Path to the web directory
        /// </summary>
        public string Web
        {
            get => _web;
			set => SetPropertyValue(ref _web, value);
        }
        private string _web = "0:/www";
    }
}