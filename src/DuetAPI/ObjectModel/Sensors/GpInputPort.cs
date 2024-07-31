namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Details about a general-purpose input port
    /// </summary>
    public partial class GpInputPort : ModelObject
    {
        /// <summary>
        /// Value of this port (0..1)
        /// </summary>
        public float Value
        {
            get => _value;
			set => SetPropertyValue(ref _value, value);
        }
        private float _value;
    }
}
