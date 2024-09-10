namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Direct-connected display screen with a ST7567 controller
    /// </summary>
    public partial class DirectDisplayScreenST7567 : DirectDisplayScreen
    {
        /// <summary>
        /// Configured contrast
        /// </summary>
        public int Contrast
        {
            get => _contrast;
            set => SetPropertyValue(ref _contrast, value);
        }
        private int _contrast = 30;

        /// <summary>
        /// Configured resistor ratio
        /// </summary>
        public int ResistorRatio
        {
            get => _resistorRatio;
            set => SetPropertyValue(ref _resistorRatio, value);
        }
        private int _resistorRatio = 6;
    }
}
