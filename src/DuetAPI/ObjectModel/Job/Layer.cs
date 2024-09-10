using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a layer from a file being printed
    /// </summary>
    public partial class Layer : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Duration of the layer (in s)
        /// </summary>
        public float Duration
        {
            get => _duration;
			set => SetPropertyValue(ref _duration, value);
        }
        private float _duration;

        /// <summary>
        /// Actual amount of filament extruded during this layer (in mm)
        /// </summary>
        public ObservableCollection<float> Filament { get; } = [];

        /// <summary>
        /// Fraction of the file printed during this layer (0..1)
        /// </summary>
        public float FractionPrinted
        {
            get => _fractionPrinted;
			set => SetPropertyValue(ref _fractionPrinted, value);
        }
        private float _fractionPrinted;

        /// <summary>
        /// Height of the layer (in mm or 0 if unknown)
        /// </summary>
        public float Height
        {
            get => _height;
			set => SetPropertyValue(ref _height, value);
        }
        private float _height;

        /// <summary>
        /// Last heater temperatures (in C or null if unknown)
        /// </summary>
        /// <seealso cref="AnalogSensor.LastReading"/>
        public ObservableCollection<float?> Temperatures { get; } = [];
    }
}