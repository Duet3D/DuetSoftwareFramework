using System;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Thermostatic parameters of a fan
    /// </summary>
    public partial class FanThermostaticControl : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// List of heaters to monitor (indices)
        /// </summary>
        /// <remarks>
        /// This is a bitmap represented as an array
        /// </remarks>
        [Obsolete("Use Sensors instead")]
        public ObservableCollection<int> Heaters { get; } = [];
        
        /// <summary>
        /// Upper temperature range required to turn on the fan (in C)
        /// </summary>
        public float? HighTemperature
        {
            get => _highTemperature;
			set => SetPropertyValue(ref _highTemperature, value);
        }
        private float? _highTemperature;

        /// <summary>
        /// Lower temperature range required to turn on the fan (in C)
        /// </summary>
        public float? LowTemperature
        {
            get => _lowTemperature;
			set => SetPropertyValue(ref _lowTemperature, value);
        }
        private float? _lowTemperature;

        /// <summary>
        /// List of sensors to monitor (indices)
        /// </summary>
        /// <remarks>
        /// This is a bitmap represented as an array
        /// </remarks>
        public ObservableCollection<int> Sensors { get; } = [];
    }
}