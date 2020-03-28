namespace DuetAPI.Machine
{
    /// <summary>
    /// Delta tower properties
    /// </summary>
    public sealed class DeltaTower : ModelObject
    {
        /// <summary>
        /// Tower position corrections (in degrees)
        /// </summary>
        public float AngleCorrection
        {
            get => _angleCorrection;
			set => SetPropertyValue(ref _angleCorrection, value);
        }
        private float _angleCorrection;

        /// <summary>
        /// Diagonal rod length (in mm)
        /// </summary>
        public float Diagonal
        {
            get => _diagonal;
			set => SetPropertyValue(ref _diagonal, value);
        }
        private float _diagonal;

        /// <summary>
        /// Deviation of the ideal endstop position (in mm)
        /// </summary>
        public float EndstopAdjustment
        {
            get => _endstopAdjustment;
			set => SetPropertyValue(ref _endstopAdjustment, value);
        }
        private float _endstopAdjustment;

        /// <summary>
        /// X coordinate of this tower (in mm)
        /// </summary>
        public float XPos
        {
            get => _xPos;
			set => SetPropertyValue(ref _xPos, value);
        }
        private float _xPos;

        /// <summary>
        /// Y coordinate of this tower (in mm)
        /// </summary>
        public float YPos
        {
            get => _yPos;
			set => SetPropertyValue(ref _yPos, value);
        }
        private float _yPos;
    }
}
