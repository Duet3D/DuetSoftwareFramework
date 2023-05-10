namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured LED strip
    /// </summary>
    public sealed class LedStrip : ModelObject
    {
        /// <summary>
        /// Type of this LED strip
        /// </summary>
        public LedStripType Type
        {
            get => _type;
            set => SetPropertyValue(ref _type, value);
        }
        private LedStripType _type = LedStripType.DotStar;

        /// <summary>
        /// Indicates if this strip is bit-banged and therefore requires motion to be stopped before sending a command
        /// </summary>
        public bool StopMovement
        {
            get => _stopMovement;
            set => SetPropertyValue(ref _stopMovement, value);
        }
        private bool _stopMovement;
    }
}
