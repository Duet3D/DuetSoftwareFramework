namespace DuetAPI.Machine
{
    /// <summary>
    /// Details about a general-purpose input port
    /// </summary>
    public sealed class GpInputPort : ModelObject
    {
        /// <summary>
        /// Indicates if the port has been configured
        /// </summary>
        public bool Configured
        {
            get => _configured;
			set => SetPropertyValue(ref _configured, value);
        }
        private bool _configured;

        /// <summary>
        /// Value of this port or null if it is not configured
        /// </summary>
        public bool? Value
        {
            get => _value;
			set => SetPropertyValue(ref _value, value);
        }
        private bool? _value;
    }
}
