namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Provides minimum, maximum and current values
    /// </summary>
    /// <typeparam name="T">ValueType of each property</typeparam>
    public sealed class MinMaxCurrent<T> : ModelObject
    {
        /// <summary>
        /// Current value
        /// </summary>
        public T Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private T _current;

        /// <summary>
        /// Minimum value
        /// </summary>
        public T Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private T _min;

        /// <summary>
        /// Maximum value
        /// </summary>
        public T Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private T _max;
    }
}
