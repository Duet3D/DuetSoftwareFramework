namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class providing information about a connected display screen
    /// </summary>
    public sealed class DirectDisplayScreen : ModelObject
    {
        /// <summary>
        /// Number of colour bits
        /// </summary>
        public int ColourBits
        {
            get => _colourBits;
            set => SetPropertyValue(ref _colourBits, value);
        }
        private int _colourBits = 1;

        /// <summary>
        /// Configured contrast (only applicable for ST7567)
        /// </summary>
        public int? Contrast
        {
            get => _contrast;
            set => SetPropertyValue(ref _contrast, value);
        }
        private int? _contrast;

        /// <summary>
        /// Display type
        /// </summary>
        public DirectDisplayController Controller
        {
            get => _controller;
            set => SetPropertyValue(ref _controller, value);
        }
        private DirectDisplayController _controller;

        /// <summary>
        /// Height of the display screen in pixels
        /// </summary>
        public int Height
        {
            get => _height;
            set => SetPropertyValue(ref _height, value);
        }
        private int _height = 64;

        /// <summary>
        /// Configured resistor ratio (only applicable for ST7567)
        /// </summary>
        public int? ResistorRatio
        {
            get => _resistorRatio;
            set => SetPropertyValue(ref _resistorRatio, value);
        }
        private int? _resistorRatio;

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
        /// Width of the display screen in pixels
        /// </summary>
        public int Width
        {
            get => _width;
            set => SetPropertyValue(ref _width, value);
        }
        private int _width = 128;
    }
}
