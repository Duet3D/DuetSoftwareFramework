namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Provides minimum, maximum and current values
    /// </summary>
    public partial class MinMaxCurrent : ModelObject
    {
        /// <summary>
        /// Current value
        /// </summary>
        public float Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private float _current;

        /// <summary>
        /// Minimum value
        /// </summary>
        public float Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private float _min;

        /// <summary>
        /// Maximum value
        /// </summary>
        public float Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private float _max;
    }
}
