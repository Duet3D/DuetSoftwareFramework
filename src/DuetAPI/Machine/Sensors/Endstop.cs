namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an endstop
    /// </summary>
    public sealed class Endstop : ModelObject
    {
        /// <summary>
        /// Whether or not the endstop is hit
        /// </summary>
        public bool Triggered
        {
            get => _triggered;
			set => SetPropertyValue(ref _triggered, value);
        }
        private bool _triggered;
        
        /// <summary>
        /// Type of the endstop
        /// </summary>
        public EndstopType Type
        {
            get => _type;
			set => SetPropertyValue(ref _type, value);
        }
        private EndstopType _type;

        /// <summary>
        /// Index of the used probe (if <see cref="Type"/> is <see cref="EndstopType.ZProbeAsEndstop"/>)
        /// </summary>
        public int? ProbeNumber
        {
            get => _probeNumber;
			set => SetPropertyValue(ref _probeNumber, value);
        }
        private int? _probeNumber;
    }
}