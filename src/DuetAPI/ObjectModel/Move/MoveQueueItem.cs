namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a DDA ring
    /// </summary>
    public sealed class MoveQueueItem : ModelObject
    {
        /// <summary>
        /// The minimum idle time before we should start a move (in s)
        /// </summary>
        public float GracePeriod { get; set; }

        /// <summary>
        /// Maximum number of moves that can be accomodated in the DDA ring
        /// </summary>
        public int Length { get; set; }
    }
}
