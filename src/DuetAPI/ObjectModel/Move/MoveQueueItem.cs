namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a DDA ring
    /// </summary>
    public partial class MoveQueueItem : ModelObject
    {
        /// <summary>
        /// The minimum idle time before we should start a move (in s)
        /// </summary>
        public float GracePeriod
        {
            get => _gracePeriod;
            set => SetPropertyValue(ref _gracePeriod, value);
        }
        private float _gracePeriod;

        /// <summary>
        /// Maximum number of moves that can be accomodated in the DDA ring
        /// </summary>
        public int Length
        {
            get => _length;
            set => SetPropertyValue(ref _length, value);
        }
        private int _length;
    }
}
