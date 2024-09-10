using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the heat subsystem
    /// </summary>
    public partial class Heat : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// List of configured bed heaters (indices)
        /// </summary>
        /// <seealso cref="Heater"/>
        /// <remarks>
        /// Items may be -1 if unconfigured
        /// </remarks>
        public ObservableCollection<int> BedHeaters { get; } = [];
        
        /// <summary>
        /// List of configured chamber heaters (indices)
        /// </summary>
        /// <seealso cref="Heater"/>
        /// <remarks>
        /// Items may be -1 if unconfigured
        /// </remarks>
        public ObservableCollection<int> ChamberHeaters { get; } = [];
        
        /// <summary>
        /// Minimum required temperature for extrusion moves (in C)
        /// </summary>
        public float ColdExtrudeTemperature
        {
            get => _coldExtrudeTemperature;
			set => SetPropertyValue(ref _coldExtrudeTemperature, value);
        }
        private float _coldExtrudeTemperature = 160F;
        
        /// <summary>
        /// Minimum required temperature for retraction moves (in C)
        /// </summary>
        public float ColdRetractTemperature
        {
            get => _coldRetractTemperature;
			set => SetPropertyValue(ref _coldRetractTemperature, value);
        }
        private float _coldRetractTemperature = 90F;
        
        /// <summary>
        /// List of configured heaters
        /// </summary>
        /// <seealso cref="Heater"/>
        public StaticModelCollection<Heater?> Heaters { get; } = [];
    }
}
