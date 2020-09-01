namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Details about a general-purpose output port
    /// </summary>
    public sealed class GpOutputPort : ModelObject
    {
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
