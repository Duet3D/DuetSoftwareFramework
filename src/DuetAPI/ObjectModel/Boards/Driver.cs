namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a driver
    /// </summary>
    public sealed class Driver : ModelObject
    {
        /// <summary>
        /// Closed-loop settings (if applicable)
        /// </summary>
        public DriverClosedLoop? ClosedLoop
        {
            get => _closedLoop;
            set => SetPropertyValue(ref _closedLoop, value);
        }
        private DriverClosedLoop? _closedLoop;

        /// <summary>
        /// Driver status register value
        /// The lowest 8 bits of these have the same bit positions as in the TMC2209 DRV_STATUS register.
        /// The TMC5160 DRV_STATUS is different so the bits are translated to this. Similarly for TMC2660.
        /// Only the lowest 16 bits are passed in driver event messages
        /// </summary>
        public uint Status
        {
            get => _status;
            set => SetPropertyValue(ref _status, value);
        }
        private uint _status;
    }
}
