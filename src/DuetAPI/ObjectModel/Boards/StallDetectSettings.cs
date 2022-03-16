namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about stall detection
    /// </summary>
    public sealed class StallDetectSettings : ModelObject
    {
        /// <summary>
        /// Stall detection threshold
        /// </summary>
        public int Threshold { get; set; }

        /// <summary>
        /// Whether the input values are filtered
        /// </summary>
        public bool Filtered { get; set; }

        /// <summary>
        /// Minimum steps
        /// </summary>
        public int MinSteps { get; set; }

        /// <summary>
        /// Coolstep register value
        /// </summary>
        public long Coolstep { get; set; }

        /// <summary>
        /// Action to perform when a stall is detected 
        /// </summary>
        public int Action { get; set; }
    }
}
