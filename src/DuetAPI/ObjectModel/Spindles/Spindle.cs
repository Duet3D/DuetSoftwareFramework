namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a CNC spindle
    /// </summary>
    public sealed class Spindle : ModelObject
    {
        /// <summary>
        /// Active RPM
        /// </summary>
        public float Active
        {
            get => _active;
			set => SetPropertyValue(ref _active, value);

        }
        private float _active;
        
        /// <summary>
        /// Current RPM, negative if anticlockwise direction
        /// </summary>
        public float Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private float _current;

        /// <summary>
        /// Frequency (in Hz)
        /// </summary>
        public int Frequency
        {
            get => _frequency;
			set => SetPropertyValue(ref _frequency, value);
        }
        private int _frequency;

        /// <summary>
        /// Minimum RPM when turned on
        /// </summary>
        public float Min
        {
            get => _min;
			set => SetPropertyValue(ref _min, value);
        }
        private float _min;

        /// <summary>
        /// Maximum RPM
        /// </summary>
        public float Max
        {
            get => _max;
			set => SetPropertyValue(ref _max, value);
        }
        private float _max = 10000F;

        /// <summary>
        /// Mapped tool number or -1 if not assigned
        /// </summary>
        public int Tool
        {
            get => _tool;
			set => SetPropertyValue(ref _tool, value);
        }
        private int _tool = -1;
    }
}