using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the SBC in SBC mode
    /// </summary>
    public partial class SBC : ModelObject
    {
        /// <summary>
        /// Indicates if AppArmor support is enabled
        /// </summary>
        /// <remarks>
        /// By default, AppArmor is required for plugin functionality
        /// </remarks>
        public bool AppArmor
        {
            get => _appArmor;
            set => SetPropertyValue(ref _appArmor, value);
        }
        private bool _appArmor;

        /// <summary>
        /// Information about the SBC's CPU
        /// </summary>
        public CPU CPU { get; } = new CPU();

        /// <summary>
        /// Information about DSF running on the SBC
        /// </summary>
        public DSF DSF { get; } = new DSF();

        /// <summary>
        /// Name and version of the system distribution or null if unknown
        /// </summary>
        public string? Distribution
        {
            get => _distribution;
            set => SetPropertyValue(ref _distribution, value);
        }
        private string? _distribution;

        /// <summary>
        /// Build datetime of the system distribution or null if unknown
        /// </summary>
        [JsonConverter(typeof(Utility.JsonOptionalShortDateTimeConverter))]
        public DateTime? DistributionBuildTime
        {
            get => _distributionBuildTime;
            set => SetPropertyValue(ref _distributionBuildTime, value);
        }
        private DateTime? _distributionBuildTime;

        /// <summary>
        /// Information about the SBC's memory (RAM)
        /// </summary>
        public Memory Memory { get; } = new Memory();

        /// <summary>
        /// SBC model or null if unknown
        /// </summary>
        public string? Model
        {
            get => _model;
            set => SetPropertyValue(ref _model, value);
        }
        private string? _model;

        /// <summary>
        /// Serial of the SBC or null if unknown
        /// </summary>
        public string? Serial
        {
            get => _serial;
            set => SetPropertyValue(ref _serial, value);
        }
        private string? _serial;

        /// <summary>
        /// Uptime of the running system (in s)
        /// </summary>
        public double? Uptime
        {
            get => _uptime;
            set => SetPropertyValue(ref _uptime, value);
        }
        private double? _uptime;
    }
}
