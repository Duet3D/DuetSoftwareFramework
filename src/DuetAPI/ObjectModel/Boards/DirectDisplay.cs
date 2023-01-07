namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class providing information about a connected display
    /// </summary>
    public sealed class DirectDisplay : ModelObject
    {
        /// <summary>
        /// Number of pulses per click of the rotary encoder
        /// </summary>
        public int PulsesPerClick
        {
            get => _pulsesPerClick;
            set => SetPropertyValue(ref _pulsesPerClick, value);
        }
        private int _pulsesPerClick;

        /// <summary>
        /// SPI frequency of the display (in Hz)
        /// </summary>
        public int SpiFreq
        {
            get => _spiFreq;
            set => SetPropertyValue(ref _spiFreq, value);
        }
        private int _spiFreq;

        /// <summary>
        /// Name of the attached display type
        /// </summary>
        public string TypeName
        {
            get => _typeName;
            set => SetPropertyValue(ref _typeName, value);
        }
        private string _typeName = string.Empty;
    }
}
