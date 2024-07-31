namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Parameters describing input shaping
    /// </summary>
    public partial class InputShaping : ModelObject
    {
        /// <summary>
        /// Amplitudes of the input shaper
        /// </summary>
        public ModelCollection<float> Amplitudes { get; } = [];

        /// <summary>
        /// Damping factor
        /// </summary>
        public float Damping
        {
            get => _damping;
            set => SetPropertyValue(ref _damping, value);
        }
        private float _damping = 0.1F;

        /// <summary>
        /// Input shaper durations (in s)
        /// </summary>
        public ModelCollection<float> Durations { get; } = [];

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
        /// Minimum fraction of the original acceleration or feed rate to which the acceleration or
        /// feed rate may be reduced in order to apply input shaping
        /// </summary>
        public float ReductionLimit
        {
            get => _reductionLimit;
            set => SetPropertyValue(ref _reductionLimit, value);
        }
        private float _reductionLimit = 0.25F;

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
