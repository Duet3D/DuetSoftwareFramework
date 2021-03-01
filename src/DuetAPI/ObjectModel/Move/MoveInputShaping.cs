namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Parameters describing input shaping
    /// </summary>
    public sealed class MoveInputShaping : ModelObject
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
        public float MinimumAcceleration
        {
            get => _minimumAcceleration;
            set => SetPropertyValue(ref _minimumAcceleration, value);
        }
        private float _minimumAcceleration = 10F;

        /// <summary>
        /// Configured input shaping type
        /// </summary>
        public MoveInputShapingType Type
        {
            get => _type;
            set => SetPropertyValue(ref _type, value);
        }
        private MoveInputShapingType _type;
    }
}
