namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Details about a general-purpose output port
    /// </summary>
    public partial class GpOutputPort : ModelObject
    {
        /// <summary>
        /// PWM frequency of this port (in Hz)
        /// </summary>
        public int Freq
        {
            get => _freq;
            set => SetPropertyValue(ref _freq, value);
        }
        private int _freq;

        /// <summary>
        /// PWM value of this port (0..1)
        /// </summary>
        public float Pwm
        {
            get => _pwm;
			set => SetPropertyValue(ref _pwm, value);
        }
        private float _pwm;
    }
}
