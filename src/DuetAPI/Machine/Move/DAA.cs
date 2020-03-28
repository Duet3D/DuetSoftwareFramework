namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about dynamic acceleration adjustment
    /// </summary>
    public sealed class DAA : ModelObject
    {
        /// <summary>
        /// Indicates if DAA is enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
			set => SetPropertyValue(ref _enabled, value);
        }
        private bool _enabled;

        /// <summary>
        /// Minimum acceleration allowed (in mm/s^2)
        /// </summary>
        public float MinimumAcceleration
        {
            get => _minimumAcceleration;
			set => SetPropertyValue(ref _minimumAcceleration, value);
        }
        private float _minimumAcceleration = 10F;

        /// <summary>
        /// Period of the ringing that is supposed to be cancelled (in s)
        /// </summary>
        /// <remarks>
        /// This is the reciprocal of the configured ringing frequency
        /// </remarks>
        public float Period
        {
            get => _period;
			set => SetPropertyValue(ref _period, value);
        }
        private float _period;
    }
}
