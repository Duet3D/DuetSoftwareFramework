namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Parameters describing input shaping
    /// </summary>
    public sealed class InputShaping : ModelObject
    {
        /// <summary>
        /// Damping factor
        /// </summary>
        public float Damping
        {
            get => _damping;
            set => SetPropertyValue(ref _damping, value);
        }
        private float _damping = 0.2F;

        /// <summary>
        /// Frequency (in Hz)
        /// </summary>
        public float Frequency
        {
            get => _frequency;
            set => SetPropertyValue(ref _frequency, value);
        }
        private float _frequency = 40F;

        /// <summary>
        /// Minimum acceleration (in mm/s)
        /// </summary>
        public float MinAcceleration
        {
            get => _minAcceleration;
            set => SetPropertyValue(ref _minAcceleration, value);
        }
        private float _minAcceleration = 10F;

        /// <summary>
        /// Configured input shaping type
        /// </summary>
        public InputShapingType Type
        {
            get => _type;
            set => SetPropertyValue(ref _type, value);
        }
        private InputShapingType _type;
    }
}
