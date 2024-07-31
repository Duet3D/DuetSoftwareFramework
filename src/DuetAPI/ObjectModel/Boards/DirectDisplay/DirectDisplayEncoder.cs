namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class providing information about a connected display encoder
    /// </summary>
    public partial class DirectDisplayEncoder : ModelObject
    {
        /// <summary>
        /// Number of pulses per click of the rotary encoder
        /// </summary>
        public int PulsesPerClick
        {
            get => _pulsesPerClick;
            set => SetPropertyValue(ref _pulsesPerClick, value);
        }
        private int _pulsesPerClick = 1;
    }
}
