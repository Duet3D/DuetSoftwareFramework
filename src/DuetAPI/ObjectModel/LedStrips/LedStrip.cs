namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured LED strip
    /// </summary>
    public sealed class LedStrip : ModelObject
    {
        /// <summary>
        /// Board address of the corresponding pin
        /// </summary>
        public int Board
        {
            get => _board;
            set => SetPropertyValue(ref _board, value);
        }
        private int _board;

        /// <summary>
        /// Name of the pin this LED strip is connected to
        /// </summary>
        public string Pin
        {
            get => _pin;
            set => SetPropertyValue(ref _pin, value);
        }
        private string _pin = string.Empty;

        /// <summary>
        /// Indicates if this strip is bit-banged and therefore requires motion to be stopped before sending a command
        /// </summary>
        public bool StopMovement
        {
            get => _stopMovement;
            set => SetPropertyValue(ref _stopMovement, value);
        }
        private bool _stopMovement;

        /// <summary>
        /// Type of this LED strip
        /// </summary>
        public LedStripType Type
        {
            get => _type;
            set => SetPropertyValue(ref _type, value);
        }
        private LedStripType _type = LedStripType.DotStar;
    }
}
